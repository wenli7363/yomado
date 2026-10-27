using System.Windows;
using System.Windows.Controls;
using Yomado.Core;

namespace Yomado.App;

public sealed class ProfileDialog : Window
{
    public DisplayProfile? Result { get; private set; }
    public ProfileDialog(DisplayProfile profile, bool active, bool create)
    {
        Title = create ? "新建屏幕" : active ? "重命名屏幕" : "编辑屏幕";
        Width = 430; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PanelBackground"); SetResourceReference(ForegroundProperty, "PanelForeground");
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "屏幕名称", Margin = new Thickness(0, 0, 0, 6) });
        var name = new TextBox { Text = profile.Name, MaxLength = 60 }; panel.Children.Add(name);
        var resolution = new ComboBox { ItemsSource = new[] { "1920 × 1080", "2560 × 1440" }, SelectedIndex = profile.Width == 2560 ? 1 : 0, IsEnabled = !active };
        var hiDpi = new CheckBox { Content = "HiDPI · 2 倍像素", IsChecked = profile.HiDpi, IsEnabled = !active, Margin = new Thickness(0, 16, 0, 0) };
        if (!active)
        {
            panel.Children.Add(new TextBlock { Text = "桌面分辨率", Margin = new Thickness(0, 18, 0, 6) });
            panel.Children.Add(resolution); panel.Children.Add(hiDpi);
        }
        panel.Children.Add(new TextBlock
        {
            Text = active ? "屏幕运行时只能修改名称；关闭后可调整分辨率与 HiDPI。" : create ?
                "保存后立即启动。Windows 的每屏缩放可在系统显示设置中调整。" : "保存供下次启动使用，不会自动创建虚拟屏。",
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = create ? "创建并启动" : "保存", IsDefault = true };
        save.Click += (_, _) =>
        {
            var proposed = active ? profile with { Name = name.Text.Trim() } :
                new DisplayProfile(profile.Slot, name.Text.Trim(), resolution.SelectedIndex == 1 ? 2560 : 1920,
                    resolution.SelectedIndex == 1 ? 1440 : 1080, hiDpi.IsChecked == true);
            if (!proposed.IsValid) { MessageBox.Show(this, "请输入 1 至 60 个字符的屏幕名称。", "yomado"); return; }
            Result = proposed; DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
