#import <AppKit/AppKit.h>

NS_ASSUME_NONNULL_BEGIN

// Presentation only. Inventory, permission checks, and window moves belong to the controller.
// Use displayID == 0 for a physical-display return target.
@interface YMAppWorkspaceView : NSView
// Defer inventory UI refreshes while a native drag or workspace menu is tracking.
// Do not use this to reject the drop action itself.
+ (BOOL)hasActiveInteraction;
@property(nonatomic) uint32_t displayID;
@property(nonatomic) BOOL physicalDisplay;
// Destination admission is separate from dragging existing windows out for recovery.
@property(nonatomic) BOOL acceptsApplications;
@property(nonatomic) CGFloat displayAspectRatio;
@property(nonatomic, copy) NSString *displayName;
@property(nonatomic, copy) NSString *displayDetail;
@property(nonatomic, copy) NSString *statusText;
@property(nonatomic, copy) NSString *contextNote;

// Payload: applicationURL (NSURL, optional), bundleIdentifier (NSString, optional),
// sourceDisplayID (NSNumber, internal drags only). The receiving view supplies the target ID.
@property(nonatomic, copy, nullable) void (^onDropApplication)(NSDictionary *payload);
@property(nonatomic, copy, nullable) void (^onChooseApplication)(void);
@property(nonatomic, copy, nullable) void (^onManageDisplay)(void);
@property(nonatomic, copy, nullable) void (^onRestoreBundleIdentifier)(NSString *bundleIdentifier);

// Rows: appName, bundleIdentifier, pid, windowCount, managedWindowCount, appURL (NSURL),
// and optional appIcon (NSImage). The counts describe detected visible windows, not GPU activity.
- (void)updateRows:(NSArray<NSDictionary *> *)rows
virtualDisplayAvailable:(BOOL)available
 accessibilityTrusted:(BOOL)trusted;
@end

NS_ASSUME_NONNULL_END
