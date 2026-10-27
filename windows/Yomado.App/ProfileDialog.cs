using System.Windows;
using System.Windows.Controls;
using Yomado.Core;

namespace Yomado.App;

public sealed class ProfileDialog : Window
{
    public DisplayProfile? Result { get; private set; }
    public ProfileDialog(DisplayProfile profile, bool active, bool create)
    {
        Title = create ? "新建虚拟屏" : "屏幕设置";
        Width = 410; Height = 340; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PanelBackground"); SetResourceReference(ForegroundProperty, "PanelForeground");
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "屏幕名称", Margin = new Thickness(0, 0, 0, 6) });
        var name = new TextBox { Text = profile.Name, MaxLength = 60 }; panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "桌面分辨率", Margin = new Thickness(0, 18, 0, 6) });
        var resolution = new ComboBox { ItemsSource = new[] { "1920 × 1080", "2560 × 1440" }, SelectedIndex = profile.Width == 2560 ? 1 : 0, IsEnabled = !active };
        panel.Children.Add(resolution);
        var hiDpi = new CheckBox { Content = "HiDPI · 2 倍像素", IsChecked = profile.HiDpi, IsEnabled = !active, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(hiDpi);
        if (active) panel.Children.Add(new TextBlock { Text = "关闭此虚拟屏后可以调整分辨率。", FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = create ? "创建" : "保存", IsDefault = true };
        save.Click += (_, _) =>
        {
            var proposed = new DisplayProfile(profile.Slot, name.Text.Trim(), resolution.SelectedIndex == 1 ? 2560 : 1920,
                resolution.SelectedIndex == 1 ? 1440 : 1080, hiDpi.IsChecked == true);
            if (!proposed.IsValid) { MessageBox.Show(this, "请输入 1 至 60 个字符的屏幕名称。", "yomado"); return; }
            Result = proposed; DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
