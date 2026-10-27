#import "WindowRecovery.h"
#import "WindowRecoveryPolicy.h"
#import <AppKit/AppKit.h>
#import <ApplicationServices/ApplicationServices.h>
#import <sys/stat.h>

NSString * const YMWindowRecoveryErrorDomain = @"dev.chekusu.yomado.window-recovery";

static AXUIElementRef YMElement(id value) {
    return value && CFGetTypeID((__bridge CFTypeRef)value) == AXUIElementGetTypeID()
        ? (__bridge AXUIElementRef)value : NULL;
}

static id YMReadAX(id element, CFStringRef attribute) {
    AXUIElementRef ref = YMElement(element);
    if (!ref) return nil;
    CFTypeRef value = NULL;
    if (AXUIElementCopyAttributeValue(ref, attribute, &value) != kAXErrorSuccess) return nil;
    return CFBridgingRelease(value);
}

static BOOL YMIsDialog(id element) {
    NSString *role = YMReadAX(element, kAXRoleAttribute);
    NSString *subrole = YMReadAX(element, kAXSubroleAttribute);
    return YMRecoveryDialogRoleSupported(role, subrole);
}

static BOOL YMIsOrdinaryWindow(id element) {
    return [YMReadAX(element, kAXRoleAttribute) isEqualToString:(__bridge NSString *)kAXWindowRole] &&
        !YMIsDialog(element) &&
        ![YMReadAX(element, kAXSubroleAttribute) isEqualToString:(__bridge NSString *)kAXFloatingWindowSubrole];
}

static NSArray *YMDirectSheets(id element) {
    id children = YMReadAX(element, kAXChildrenAttribute);
    if (![children isKindOfClass:NSArray.class]) return @[];
    NSMutableArray *sheets = [NSMutableArray array];
    for (id child in [children subarrayWithRange:NSMakeRange(0, MIN(32, [children count]))]) {
        if ([YMReadAX(child, kAXRoleAttribute) isEqualToString:(__bridge NSString *)kAXSheetRole])
            [sheets addObject:child];
        if (sheets.count == 4) break;
    }
    return sheets;
}

static NSDictionary *YMWindowFrame(id element) {
    id positionValue = YMReadAX(element, kAXPositionAttribute);
    id sizeValue = YMReadAX(element, kAXSizeAttribute);
    if (!positionValue || !sizeValue ||
        CFGetTypeID((__bridge CFTypeRef)positionValue) != AXValueGetTypeID() ||
        CFGetTypeID((__bridge CFTypeRef)sizeValue) != AXValueGetTypeID()) return nil;
    AXValueRef position = (__bridge AXValueRef)positionValue, size = (__bridge AXValueRef)sizeValue;
    CGPoint point = CGPointZero; CGSize dimensions = CGSizeZero;
    if (AXValueGetType(position) != kAXValueCGPointType || AXValueGetType(size) != kAXValueCGSizeType ||
        !AXValueGetValue(position, kAXValueCGPointType, &point) ||
        !AXValueGetValue(size, kAXValueCGSizeType, &dimensions)) return nil;
    return YMRecoveryGeometry(@{@"x": @(point.x), @"y": @(point.y),
                                 @"width": @(dimensions.width), @"height": @(dimensions.height)});
}

static NSError *YMRecoveryError(NSInteger code, NSString *message) {
    // Do not include AX descriptions, application text or filesystem paths.
    return [NSError errorWithDomain:YMWindowRecoveryErrorDomain code:code
                          userInfo:@{NSLocalizedDescriptionKey: message}];
}

@interface YMWindowRecovery ()
@property(nonatomic, copy, readwrite) NSString *statusText;
@property(nonatomic, readwrite) BOOL trackedWindowOnVirtual;
@property(nonatomic, readwrite) BOOL automaticRecoveryPaused;
@property(nonatomic, copy) NSURL *checkpointURL;
@property(nonatomic, copy) NSDictionary *checkpoint;
@property(nonatomic, copy) NSArray<NSDictionary *> *displays;
@property(nonatomic) CGDirectDisplayID virtualDisplayID;
@property(nonatomic, strong) id trackedWindow;
@property(nonatomic, strong) id recoveryDialog;
@property(nonatomic) pid_t trackedPID;
@property(nonatomic) NSTimeInterval lastPoll;
@property(nonatomic) NSTimeInterval lastMutation;
@property(nonatomic) BOOL operationInProgress;
- (BOOL)captureWeChatPlacementFromDisplayID:(CGDirectDisplayID)sourceDisplayID error:(NSError **)error;
- (BOOL)performMoveWeChatToVirtualFromDisplayID:(CGDirectDisplayID)sourceDisplayID error:(NSError **)error;
- (BOOL)performRestoreWeChat:(NSError **)error;
@end

@implementation YMWindowRecovery
- (instancetype)initWithCheckpointURL:(NSURL *)URL {
    self = [super init];
    if (!self) return nil;
    if (!URL) {
        NSURL *support = [NSFileManager.defaultManager URLsForDirectory:NSApplicationSupportDirectory
                                                             inDomains:NSUserDomainMask].firstObject;
        URL = [[support URLByAppendingPathComponent:@"dev.chekusu.yomado" isDirectory:YES]
               URLByAppendingPathComponent:@"wechat-window-placement.json"];
    }
    _checkpointURL = [URL copy];
    _displays = @[];
    _virtualDisplayID = kCGNullDirectDisplay;
    _statusText = @"只记住微信窗口的位置；遇到登录问题可返回原实体屏。";
    NSDictionary *attributes = [NSFileManager.defaultManager attributesOfItemAtPath:URL.path error:nil];
    if ([attributes fileSize] <= 8192) {
        NSData *data = [NSData dataWithContentsOfURL:URL options:0 error:nil];
        if (data) _checkpoint = YMRecoveryCanonicalCheckpoint(
            [NSJSONSerialization JSONObjectWithData:data options:0 error:nil]);
    }
    if (_checkpoint) {
        BOOL pending = [_checkpoint[@"recoveryPending"] boolValue];
        _trackedWindowOnVirtual = pending;
        _statusText = pending ? @"有待恢复的微信窗口记录；可返回原实体屏。"
                              : @"已记住微信的实体屏位置。";
    }
    return self;
}

- (BOOL)accessibilityTrusted { return AXIsProcessTrusted(); }
- (BOOL)hasCheckpoint { return self.checkpoint != nil; }
- (BOOL)needsRestoreBeforeRemovingVirtualDisplay { return [self.checkpoint[@"recoveryPending"] boolValue]; }
- (BOOL)canMove { return self.accessibilityTrusted && self.virtualDisplayID != kCGNullDirectDisplay && !self.needsRestoreBeforeRemovingVirtualDisplay && !self.operationInProgress; }
- (BOOL)canRestore { return self.accessibilityTrusted && self.hasCheckpoint && !self.operationInProgress; }

- (void)refreshWithVirtualDisplayID:(CGDirectDisplayID)displayID displaySnapshot:(NSArray<NSDictionary *> *)snapshot {
    NSAssert(NSThread.isMainThread, @"Window recovery is main-thread only.");
    self.virtualDisplayID = displayID;
    NSMutableArray *normalized = [NSMutableArray array];
    for (id raw in snapshot) {
        if (![raw isKindOfClass:NSDictionary.class] || ![raw[@"displayID"] isKindOfClass:NSNumber.class]) continue;
        CGDirectDisplayID currentID = [raw[@"displayID"] unsignedIntValue];
        NSDictionary *bounds = YMRecoveryGeometry(raw[@"bounds"]);
        if (!bounds) bounds = YMRecoveryGeometry(@{@"x": raw[@"x"] ?: NSNull.null,
            @"y": raw[@"y"] ?: NSNull.null, @"width": raw[@"logicalWidth"] ?: NSNull.null,
            @"height": raw[@"logicalHeight"] ?: NSNull.null});
        if (!bounds) continue;
        BOOL virtualDisplay = currentID == displayID || [raw[@"isVirtual"] boolValue];
        NSString *uuid = [raw[@"displayUUID"] isKindOfClass:NSString.class] ? raw[@"displayUUID"] : nil;
        if (!virtualDisplay && !uuid) {
            CFUUIDRef identifier = CGDisplayCreateUUIDFromDisplayID(currentID);
            if (identifier) {
                uuid = CFBridgingRelease(CFUUIDCreateString(kCFAllocatorDefault, identifier));
                CFRelease(identifier);
            }
        }
        [normalized addObject:@{@"displayID": @(currentID), @"bounds": bounds,
            @"displayUUID": uuid ?: @"", @"isActive": @([raw[@"isActive"] boolValue]),
            @"isOnline": @([raw[@"isOnline"] boolValue]), @"isMain": @([raw[@"isMain"] boolValue]),
            @"isVirtual": @(virtualDisplay)}];
    }
    self.displays = normalized;
}

- (BOOL)openAccessibilitySettings:(NSError **)error {
    NSAssert(NSThread.isMainThread, @"Window recovery is main-thread only.");
    // Request registration only on this explicit action. Never alter TCC.
    (void)AXIsProcessTrustedWithOptions((__bridge CFDictionaryRef)
        @{(__bridge NSString *)kAXTrustedCheckOptionPrompt: @YES});
    NSURL *URL = [NSURL URLWithString:@"x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension?Privacy_Accessibility"];
    BOOL opened = [NSWorkspace.sharedWorkspace openURL:URL];
    if (!opened) {
        NSURL *fallback = [NSURL URLWithString:@"x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"];
        opened = [NSWorkspace.sharedWorkspace openURL:fallback];
    }
    if (!opened && error) *error = YMRecoveryError(2,
        @"无法打开系统设置。请在“隐私与安全”中找到“辅助功能”或“设备控制和数据访问”，开启 yomado。");
    return opened;
}

- (BOOL)fail:(NSError **)error code:(NSInteger)code message:(NSString *)message pause:(BOOL)pause {
    self.statusText = message;
    if (pause) self.automaticRecoveryPaused = YES;
    if (error) *error = YMRecoveryError(code, message);
    return NO;
}

- (BOOL)requirePermission:(NSError **)error {
    if (self.accessibilityTrusted) return YES;
    return [self fail:error code:1 message:@"移动微信需要辅助功能权限；请先选择“打开控制权限设置”。" pause:YES];
}

- (id)wechatApplicationWithPID:(pid_t *)PID {
    for (NSRunningApplication *application in
            [NSRunningApplication runningApplicationsWithBundleIdentifier:YMRecoveryWeChatBundleID]) {
        if (application.terminated || !YMRecoverySupportedBundle(application.bundleIdentifier)) continue;
        if (PID) *PID = application.processIdentifier;
        AXUIElementRef element = AXUIElementCreateApplication(application.processIdentifier);
        if (element) AXUIElementSetMessagingTimeout(element, 1.0);
        return CFBridgingRelease(element);
    }
    return nil;
}

- (NSArray *)windowsForApplication:(id)application {
    id windows = YMReadAX(application, kAXWindowsAttribute);
    return [windows isKindOfClass:NSArray.class] ? windows : @[];
}

- (id)ordinaryWindowForApplication:(id)application {
    id candidate = YMReadAX(application, kAXMainWindowAttribute);
    if (YMIsOrdinaryWindow(candidate) && YMWindowFrame(candidate)) return candidate;
    candidate = YMReadAX(application, kAXFocusedWindowAttribute);
    if (YMIsOrdinaryWindow(candidate) && YMWindowFrame(candidate)) return candidate;
    id best = nil; double area = 0;
    for (id window in [self windowsForApplication:application]) {
        if (!YMIsOrdinaryWindow(window)) continue;
        NSDictionary *frame = YMWindowFrame(window);
        double candidateArea = [frame[@"width"] doubleValue] * [frame[@"height"] doubleValue];
        if (candidateArea > area) { best = window; area = candidateArea; }
    }
    return best;
}

- (BOOL)persistCheckpoint:(NSDictionary *)record error:(NSError **)error {
    NSDictionary *safe = YMRecoveryCanonicalCheckpoint(record);
    NSData *data = safe ? [NSJSONSerialization dataWithJSONObject:safe options:NSJSONWritingSortedKeys error:nil] : nil;
    if (!data) return [self fail:error code:2 message:@"窗口位置记录无效，尚未移动微信。" pause:YES];
    NSURL *directory = self.checkpointURL.URLByDeletingLastPathComponent;
    BOOL exists = [NSFileManager.defaultManager createDirectoryAtURL:directory
        withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions: @0700} error:nil];
    if (!exists || chmod(directory.fileSystemRepresentation, 0700) != 0 ||
        ![data writeToURL:self.checkpointURL options:NSDataWritingAtomic error:nil] ||
        chmod(self.checkpointURL.fileSystemRepresentation, 0600) != 0) {
        return [self fail:error code:3 message:@"无法安全保存窗口位置，尚未继续移动微信。" pause:YES];
    }
    self.checkpoint = safe;
    return YES;
}

- (BOOL)captureWeChatPlacement:(NSError **)error {
    return [self captureWeChatPlacementFromDisplayID:kCGNullDirectDisplay error:error];
}

- (BOOL)captureWeChatPlacementFromDisplayID:(CGDirectDisplayID)sourceDisplayID error:(NSError **)error {
    NSAssert(NSThread.isMainThread, @"Window recovery is main-thread only.");
    if (self.needsRestoreBeforeRemovingVirtualDisplay)
        return [self fail:error code:16 message:@"请先完成微信窗口恢复，再记忆或移入新的窗口；原位置记录保持不变。" pause:NO];
    if (![self requirePermission:error]) return NO;
    pid_t PID = 0;
    id application = [self wechatApplicationWithPID:&PID];
    if (!application) return [self fail:error code:4 message:@"微信尚未运行；请先打开微信。" pause:NO];
    id window = [self ordinaryWindowForApplication:application];
    if (!window) return [self fail:error code:5 message:@"找不到可记忆的微信主窗口；请先在实体屏打开微信主界面。" pause:YES];
    NSDictionary *frame = YMWindowFrame(window);
    if (sourceDisplayID) {
        NSDictionary *source = YMRecoveryDominantDisplay(frame, self.displays);
        if (!source || [source[@"isVirtual"] boolValue] || [source[@"displayID"] unsignedIntValue] != sourceDisplayID)
            return [self fail:error code:19 message:@"微信主窗口不在所拖出的实体屏中；尚未移动窗口，也未改写原位置记录。请从主窗口所在屏幕拖动。" pause:NO];
    }
    NSDictionary *record = YMRecoveryCheckpointForFrame(YMRecoveryWeChatBundleID, frame, self.displays, NO);
    if (!record) return [self fail:error code:6 message:@"请先把微信主窗口放在实体屏；虚拟屏位置不会覆盖恢复记录。" pause:NO];
    if (![self persistCheckpoint:record error:error]) return NO;
    self.trackedWindow = window; self.trackedPID = PID;
    self.trackedWindowOnVirtual = NO;
    self.automaticRecoveryPaused = NO;
    self.recoveryDialog = nil;
    self.statusText = @"已记住微信主窗口的实体屏和位置。";
    return YES;
}

- (BOOL)mutationAllowed:(NSError **)error {
    NSTimeInterval now = NSDate.timeIntervalSinceReferenceDate;
    if (self.lastMutation > 0 && now - self.lastMutation < 2.5)
        return [self fail:error code:7 message:@"请稍候，观察微信窗口后再进行下一步。" pause:NO];
    self.lastMutation = now;
    return YES;
}

- (BOOL)virtualDestinationStillUsable:(NSDictionary *)display {
    CGDirectDisplayID targetID = [display[@"displayID"] unsignedIntValue];
    if (targetID != self.virtualDisplayID) return YES; // Recovery to the user's physical screen stays available.
    if (!CGDisplayIsOnline(targetID) || !CGDisplayIsActive(targetID) || CGDisplayIsInMirrorSet(targetID)
        || CGDisplayMirrorsDisplay(targetID) != kCGNullDirectDisplay) return NO;
    CGRect bounds = CGDisplayBounds(targetID);
    return CGRectEqualToRect(bounds, CGRectMake([display[@"bounds"][@"x"] doubleValue],
        [display[@"bounds"][@"y"] doubleValue], [display[@"bounds"][@"width"] doubleValue], [display[@"bounds"][@"height"] doubleValue]));
}
- (BOOL)placeWindow:(id)window frame:(NSDictionary *)frame display:(NSDictionary *)display error:(NSError **)error {
    if (![self virtualDestinationStillUsable:display])
        return [self fail:error code:17 message:@"虚拟屏显示配置已变化；已停止移入窗口。" pause:YES];
    AXUIElementRef element = YMElement(window);
    NSDictionary *current = YMWindowFrame(window);
    pid_t PID = 0;
    if (!element || AXUIElementGetPid(element, &PID) != kAXErrorSuccess ||
        !YMRecoverySupportedBundle([NSRunningApplication runningApplicationWithProcessIdentifier:PID].bundleIdentifier) ||
        !current || !YMRecoveryGeometry(frame) || !YMRecoveryUsableDisplay(display))
        return [self fail:error code:8 message:@"微信窗口当前不可访问；已停止自动尝试。" pause:YES];
    AXUIElementSetMessagingTimeout(element, 1.0);
    CGSize size = CGSizeMake([frame[@"width"] doubleValue], [frame[@"height"] doubleValue]);
    // Resizing is best effort. A login dialog may have a fixed size, but must
    // still be moved back to the user. Never abandon recovery on size refusal.
    Boolean canResize = false;
    if ((fabs([current[@"width"] doubleValue] - size.width) > 1 ||
         fabs([current[@"height"] doubleValue] - size.height) > 1) &&
        AXUIElementIsAttributeSettable(element, kAXSizeAttribute, &canResize) == kAXErrorSuccess && canResize) {
        AXValueRef sizeValue = AXValueCreate(kAXValueCGSizeType, &size);
        if (sizeValue) {
            (void)AXUIElementSetAttributeValue(element, kAXSizeAttribute, sizeValue);
            CFRelease(sizeValue);
        }
    }
    NSDictionary *resized = YMWindowFrame(window) ?: current;
    NSDictionary *bounds = display[@"bounds"];
    NSDictionary *effective = YMRecoveryFrameOnDisplay(@{
        @"x": @([frame[@"x"] doubleValue] - [bounds[@"x"] doubleValue]),
        @"y": @([frame[@"y"] doubleValue] - [bounds[@"y"] doubleValue]),
        @"width": resized[@"width"], @"height": resized[@"height"]}, display);
    if (![self virtualDestinationStillUsable:display])
        return [self fail:error code:17 message:@"虚拟屏显示配置已变化；已停止移入窗口。" pause:YES];
    CGPoint position = CGPointMake([effective[@"x"] doubleValue], [effective[@"y"] doubleValue]);
    AXValueRef value = AXValueCreate(kAXValueCGPointType, &position);
    AXError result = value ? AXUIElementSetAttributeValue(element, kAXPositionAttribute, value) : kAXErrorFailure;
    if (value) CFRelease(value);
    if (result != kAXErrorSuccess)
        return [self fail:error code:10 message:@"微信未接受窗口移位；已停止自动尝试，可手动返回实体屏。" pause:YES];
    // Observe a short bounded settlement period. No repeated AX writes and no
    // input/focus changes are used. The operation guard prevents reentry while
    // processing run-loop callbacks. Pending recovery is kept on any failure.
    NSTimeInterval deadline = NSDate.timeIntervalSinceReferenceDate + 0.6;
    for (NSUInteger observation = 0; observation < 4; observation++) {
        NSDictionary *observed = YMWindowFrame(window);
        if (!observed || ![self virtualDestinationStillUsable:display]) break; // Stop if access or topology changed.
        if (YMRecoveryObservedFrameIsAccessible(observed, effective, display)) return YES;
        if (observation == 3 || NSDate.timeIntervalSinceReferenceDate >= deadline) break;
        [NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.15]];
    }
    return [self fail:error code:17 message:@"尚未确认微信窗口已到目标显示器；保留恢复记录并暂停自动尝试。" pause:YES];
}

- (BOOL)moveWeChatToVirtual:(NSError **)error {
    return [self moveWeChatToVirtualFromDisplayID:kCGNullDirectDisplay error:error];
}

- (BOOL)moveWeChatToVirtualFromDisplayID:(CGDirectDisplayID)sourceDisplayID error:(NSError **)error {
    NSAssert(NSThread.isMainThread, @"Window recovery is main-thread only.");
    if (self.operationInProgress) return [self fail:error code:18 message:@"正在观察微信窗口，请稍候。" pause:NO];
    self.operationInProgress = YES;
    @try { return [self performMoveWeChatToVirtualFromDisplayID:sourceDisplayID error:error]; }
    @finally { self.operationInProgress = NO; }
}

- (BOOL)performMoveWeChatToVirtualFromDisplayID:(CGDirectDisplayID)sourceDisplayID error:(NSError **)error {
    if (self.needsRestoreBeforeRemovingVirtualDisplay)
        return [self fail:error code:16 message:@"请先恢复已移入的微信窗口，再开始新的移屏；原位置记录保持不变。" pause:NO];
    if (![self requirePermission:error] || ![self mutationAllowed:error]) return NO;
    NSDictionary *target = nil;
    for (NSDictionary *display in self.displays) {
        if ([display[@"displayID"] unsignedIntValue] == self.virtualDisplayID &&
            [display[@"isVirtual"] boolValue] && YMRecoveryUsableDisplay(display)) { target = display; break; }
    }
    if (!target || self.virtualDisplayID == kCGNullDirectDisplay)
        return [self fail:error code:11 message:@"请先创建 yomado 的虚拟屏，再移动微信。" pause:NO];
    // Capture before any AX mutation. Never move if the checkpoint cannot be
    // saved safely; this also refuses a second move from a virtual position.
    if (![self captureWeChatPlacementFromDisplayID:sourceDisplayID error:error]) return NO;
    NSMutableDictionary *pending = [self.checkpoint mutableCopy];
    pending[@"recoveryPending"] = @YES;
    if (![self persistCheckpoint:pending error:error]) return NO;
    NSDictionary *frame = YMRecoveryFrameOnDisplay(self.checkpoint[@"window"], target);
    if (![self placeWindow:self.trackedWindow frame:frame display:target error:error]) {
        // One return attempt after an unverified move; never a loop. A failed
        // return retains recoveryPending so stopping cannot remove the display.
        NSError *moveError = error ? *error : nil;
        NSError *returnError = nil;
        BOOL returned = [self performRestoreWeChat:&returnError];
        self.automaticRecoveryPaused = YES;
        self.statusText = returned ? @"移屏未获验证，微信已返回实体屏；本次测试已暂停。"
                                   : @"移屏及返回未获验证；保留虚拟屏和原位置记录，请手动返回原屏。";
        if (error) *error = moveError ?: returnError ?: YMRecoveryError(17, self.statusText);
        return NO;
    }
    self.trackedWindowOnVirtual = YES;
    self.automaticRecoveryPaused = NO;
    self.lastPoll = NSDate.timeIntervalSinceReferenceDate;
    self.statusText = @"微信已移入虚拟屏；只监测可识别的安全对话框，其他注销情形请手动返回原屏。";
    return YES;
}

- (BOOL)restoreWeChat:(NSError **)error {
    NSAssert(NSThread.isMainThread, @"Window recovery is main-thread only.");
    if (self.operationInProgress) return [self fail:error code:18 message:@"正在观察微信窗口，请稍候。" pause:NO];
    self.operationInProgress = YES;
    @try { return [self performRestoreWeChat:error]; }
    @finally { self.operationInProgress = NO; }
}

- (BOOL)performRestoreWeChat:(NSError **)error {
    if (!self.checkpoint) { self.statusText = @"没有需要恢复的微信位置记录。"; return YES; }
    if (![self requirePermission:error]) return NO;
    NSDictionary *target = YMRecoveryRestoreDisplay(self.checkpoint, self.displays);
    NSDictionary *mainFrame = YMRecoveryRestoreFrame(self.checkpoint, self.displays);
    if (!target || !mainFrame)
        return [self fail:error code:12 message:@"没有可用的实体显示器；保留虚拟屏及恢复记录。" pause:YES];
    pid_t PID = 0;
    id application = [self wechatApplicationWithPID:&PID];
    if (!application) {
        NSMutableDictionary *complete = [self.checkpoint mutableCopy]; complete[@"recoveryPending"] = @NO;
        if (![self persistCheckpoint:complete error:error]) return NO;
        self.trackedWindowOnVirtual = NO; self.trackedWindow = nil; self.recoveryDialog = nil;
        self.statusText = @"微信已关闭；保留实体屏位置供下次使用。";
        return YES;
    }
    id main = self.trackedPID == PID && YMWindowFrame(self.trackedWindow) ? self.trackedWindow : nil;
    if (!main) main = [self ordinaryWindowForApplication:application];
    // If only the login/security window remains, it too must be made reachable.
    // Selecting its geometry does not read its title, text or login fields.
    if (!main) {
        main = YMReadAX(application, kAXMainWindowAttribute);
        if (!YMWindowFrame(main)) main = nil;
    }
    if (!main) {
        for (id window in [self windowsForApplication:application]) {
            if (YMWindowFrame(window)) { main = window; break; }
        }
    }
    if (!main) return [self fail:error code:13 message:@"微信暂时没有可访问的窗口；保留虚拟屏并停止自动尝试。" pause:YES];
    NSMutableArray *windows = [NSMutableArray arrayWithObject:main];
    // An explicitly recognized safety dialog and sheets attached to the tracked
    // main window may need their own move. Unrelated WeChat windows stay put.
    if (YMWindowFrame(self.recoveryDialog) && ![windows containsObject:self.recoveryDialog])
        [windows addObject:self.recoveryDialog];
    id sheets = YMDirectSheets(main);
    if ([sheets isKindOfClass:NSArray.class]) {
        for (id sheet in [sheets subarrayWithRange:NSMakeRange(0, MIN(4, [sheets count]))])
            if (YMIsDialog(sheet) && YMWindowFrame(sheet) && ![windows containsObject:sheet]) [windows addObject:sheet];
    }
    // Recovery intentionally bypasses the move cooldown: a security prompt
    // should be returned to the user's physical desktop immediately.
    self.lastMutation = NSDate.timeIntervalSinceReferenceDate;
    for (id window in windows) {
        NSDictionary *frame = mainFrame;
        // A replacement login window or a modal retains its own dimensions.
        // Only the same tracked ordinary main window reuses its saved size.
        if (window != main || window != self.trackedWindow || !YMIsOrdinaryWindow(window)) {
            NSDictionary *current = YMWindowFrame(window);
            frame = YMRecoveryFrameOnDisplay(@{@"x": self.checkpoint[@"window"][@"x"],
                @"y": self.checkpoint[@"window"][@"y"], @"width": current[@"width"], @"height": current[@"height"]}, target);
        }
        if (![self placeWindow:window frame:frame display:target error:error]) return NO;
    }
    NSMutableDictionary *complete = [self.checkpoint mutableCopy]; complete[@"recoveryPending"] = @NO;
    if (![self persistCheckpoint:complete error:error]) return NO;
    self.trackedWindowOnVirtual = NO; self.trackedWindow = main; self.trackedPID = PID;
    self.recoveryDialog = nil;
    self.statusText = @"微信已返回原实体屏；原显示器不可用时会放到当前实体主屏。";
    return YES;
}

// This is intentionally not a recursive AX tree walk. It reads at most two
// shallow child levels of a verified dialog/sheet, and only static-text values
// or button titles. Text fields, ordinary windows and chat descendants are never
// inspected. No text is persisted, logged, exposed in errors or sent anywhere.
- (BOOL)dialogHasSupportedSafetyPrompt:(id)dialog {
    if (!YMIsDialog(dialog)) return NO;
    NSDictionary *frame = YMWindowFrame(dialog);
    if (!frame || [frame[@"width"] doubleValue] > 1000 || [frame[@"height"] doubleValue] > 800) return NO;
    NSMutableArray *nodes = [NSMutableArray array];
    id first = YMReadAX(dialog, kAXChildrenAttribute);
    if ([first isKindOfClass:NSArray.class])
        [nodes addObjectsFromArray:[first subarrayWithRange:NSMakeRange(0, MIN(12, [first count]))]];
    NSUInteger firstCount = nodes.count;
    for (NSUInteger index = 0; index < firstCount && nodes.count < 24; index++) {
        id node = nodes[index];
        NSString *role = YMReadAX(node, kAXRoleAttribute);
        // Descend through one generic dialog group only; never through an
        // editable input, list, table, scroll area or embedded application.
        if (![role isEqualToString:(__bridge NSString *)kAXGroupRole]) continue;
        id children = YMReadAX(node, kAXChildrenAttribute);
        if ([children isKindOfClass:NSArray.class])
            [nodes addObjectsFromArray:[children subarrayWithRange:NSMakeRange(0, MIN(24 - nodes.count, [children count]))]];
    }
    for (id node in nodes) {
        NSString *role = YMReadAX(node, kAXRoleAttribute);
        id text = nil;
        if ([role isEqualToString:(__bridge NSString *)kAXStaticTextRole]) text = YMReadAX(node, kAXValueAttribute);
        else if ([role isEqualToString:(__bridge NSString *)kAXButtonRole]) text = YMReadAX(node, kAXTitleAttribute);
        if (YMRecoveryIsSupportedSafetyText(text)) return YES;
    }
    return NO;
}

- (BOOL)pollForRecovery:(NSError **)error {
    NSAssert(NSThread.isMainThread, @"Window recovery is main-thread only.");
    if (self.operationInProgress) return YES;
    NSTimeInterval now = NSDate.timeIntervalSinceReferenceDate;
    if (!YMRecoveryCanPoll(self.needsRestoreBeforeRemovingVirtualDisplay, self.automaticRecoveryPaused, self.lastPoll, now)) return YES;
    self.lastPoll = now;
    if (![self requirePermission:error]) return NO;
    pid_t PID = 0;
    id application = [self wechatApplicationWithPID:&PID];
    if (!application) return YES; // Closing WeChat must not automatically relaunch it.
    if (self.trackedPID && self.trackedPID != PID) {
        self.trackedWindow = nil; self.recoveryDialog = nil;
        return [self fail:error code:14 message:@"微信进程已变化；已暂停自动恢复，请选择“返回原显示器”。" pause:YES];
    }
    CFTypeRef rawWindows = NULL;
    AXError windowResult = AXUIElementCopyAttributeValue(YMElement(application), kAXWindowsAttribute, &rawWindows);
    id currentWindows = CFBridgingRelease(rawWindows);
    if (windowResult != kAXErrorSuccess || ![currentWindows isKindOfClass:NSArray.class])
        return [self fail:error code:15 message:@"微信窗口列表暂时不可访问；已暂停自动检查，请手动返回原屏。" pause:YES];
    NSMutableArray *candidates = [NSMutableArray array];
    for (id window in currentWindows) if (YMIsDialog(window)) [candidates addObject:window];
    id main = self.trackedWindow ?: [self ordinaryWindowForApplication:application];
    id sheets = YMDirectSheets(main);
    if ([sheets isKindOfClass:NSArray.class]) {
        for (id sheet in [sheets subarrayWithRange:NSMakeRange(0, MIN(4, [sheets count]))])
            if (YMIsDialog(sheet) && ![candidates containsObject:sheet]) [candidates addObject:sheet];
    }
    for (id dialog in [candidates subarrayWithRange:NSMakeRange(0, MIN(8, candidates.count))]) {
        if (![self dialogHasSupportedSafetyPrompt:dialog]) continue;
        self.recoveryDialog = dialog;
        BOOL restored = [self restoreWeChat:error];
        // A recognized security prompt ends this trial, even if restoration
        // succeeds. Only a fresh explicit move may resume background checking.
        self.automaticRecoveryPaused = YES;
        if (restored) self.statusText = @"检测到受支持的安全登录提示；微信已返回实体屏，自动尝试已暂停，请亲自处理登录。";
        return restored;
    }
    return YES;
}
@end
