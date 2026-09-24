using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YumeShelf.Common;
using YumeShelf.Application.AI;

namespace YumeShelf.Presentation;
public partial class AiAssistantPage : System.Windows.Controls.UserControl
{
    private bool _composing;
    public AiAssistantPage()
    {
        InitializeComponent();
        System.Windows.DataObject.AddPastingHandler(QueryEditor, QueryPasting);
        TextCompositionManager.AddPreviewTextInputStartHandler(QueryEditor, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(QueryEditor, (_, _) => _composing = true);
        QueryEditor.PreviewTextInput += (_, _) => Dispatcher.BeginInvoke(() => _composing = false, DispatcherPriority.Background);
        QueryEditor.LostKeyboardFocus += (_, _) => _composing = false;
        QueryEditor.PreviewKeyDown += (_, args) =>
        {
            // TextBox disables its native Paste command for image-only clipboard data.
            // Intercept the shortcut before that command; text paste still follows the native path.
            if (args.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !_composing)
            {
                try
                {
                    if (AiClipboardImage.ContainsImage(System.Windows.Clipboard.GetDataObject()))
                    { args.Handled = true; PasteImageClicked(QueryEditor, new RoutedEventArgs()); return; }
                }
                catch (Exception ex) when (GameImageLoader.IsImageError(ex))
                {
                    args.Handled = true;
                    if (DataContext is AiAssistantViewModel imageVm) imageVm.ReportImageError("暂时无法读取剪贴板，请重试或使用“选择图片”。");
                    return;
                }
            }
            if (!ShouldSend(args.Key, Keyboard.Modifiers, _composing)) return;
            if (DataContext is AiAssistantViewModel vm && vm.SendCommand.CanExecute(null))
            { args.Handled = true; vm.SendCommand.Execute(null); }
        };
    }

    public static bool ShouldSend(Key key, ModifierKeys modifiers, bool composing)
        => key == Key.Enter && modifiers == ModifierKeys.None && !composing;

    private async void ChooseImageClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantViewModel vm || !vm.CanEditInput) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择一张游戏截图或封面",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await vm.SelectImageAsync(dialog.FileName);
        QueryEditor.Focus();
    }

    private async void PasteImageClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantViewModel vm || !vm.CanEditInput) return;
        try
        {
            var bitmap = AiClipboardImage.Read(System.Windows.Clipboard.GetDataObject());
            if (bitmap is null) vm.ReportImageError("剪贴板中没有图片，请先复制图片或使用截图工具。");
            else await vm.PasteImageAsync(bitmap);
        }
        catch (Exception ex) when (GameImageLoader.IsImageError(ex))
        { vm.ReportImageError("暂时无法读取剪贴板图片，请重试或使用“选择图片”。"); }
        QueryEditor.Focus();
    }

    private async void QueryPasting(object sender, DataObjectPastingEventArgs e)
    {
        try
        {
            if (!AiClipboardImage.ContainsImage(e.DataObject)) return;
            e.CancelCommand();
            if (DataContext is not AiAssistantViewModel editable || !editable.CanEditInput) return;
            var bitmap = AiClipboardImage.Read(e.DataObject);
            if (bitmap is null) return;
            if (DataContext is AiAssistantViewModel vm && vm.CanEditInput) await vm.PasteImageAsync(bitmap);
        }
        catch (Exception ex) when (GameImageLoader.IsImageError(ex))
        {
            e.CancelCommand();
            if (DataContext is AiAssistantViewModel vm) vm.ReportImageError("无法粘贴这张图片，请重新截图或选择图片文件。");
        }
    }

    private void CoverPreviewVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => ApplyCoverButton.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void ConversationScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ConversationScroll)) return;
        // Keep the reader's position unless they were already at the bottom.
        if (e.ExtentHeightChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 24)
            ConversationScroll.ScrollToEnd();
    }
}
