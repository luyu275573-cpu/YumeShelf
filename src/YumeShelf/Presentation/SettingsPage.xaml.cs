using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using YumeShelf.Application;

namespace YumeShelf.Presentation;

public partial class SettingsPage : System.Windows.Controls.UserControl
{
    private bool _updatingKey;

    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is SettingsViewModel old) old.PropertyChanged -= PreviewChanged;
            if (args.NewValue is not SettingsViewModel current) return;
            current.PropertyChanged += PreviewChanged;
            _updatingKey = true;
            try { AiKeyBox.Password = current.AiApiKey; }
            finally { _updatingKey = false; }
            ThemePalette.Apply(Resources, current.DraftSettings);
        };
    }

    private void PreviewChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is SettingsViewModel vm && args.PropertyName is nameof(SettingsViewModel.ColorPalette) or nameof(SettingsViewModel.NightMode))
            ThemePalette.Apply(Resources, vm.DraftSettings);
    }

    private void AiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_updatingKey && DataContext is SettingsViewModel viewModel) viewModel.AiApiKey = AiKeyBox.Password;
    }

    private void ChooseBackground(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = "选择背景图片",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ((SettingsViewModel)DataContext).SelectBackground(dialog.FileName);
    }
}
