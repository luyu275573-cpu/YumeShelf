using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YumeShelf.Application;
using YumeShelf.Application.AI;
using YumeShelf.Infrastructure;
using YumeShelf.Presentation;

internal static partial class Program
{
    private static void AiVisionChecks()
    {
        var fixture = VisionFixture();
        var original = File.ReadAllBytes(fixture);
        var image = AiImageAttachment.FromFile(fixture);
        Check(image.Width == 1600 && image.Height == 900 && image.ByteCount <= AiLimits.ImageBytes && image.Preview.IsFrozen &&
              image.Preview.PixelWidth <= 256 && image.Preview.PixelHeight <= 256, "image preparation bounds upload and thumbnail sizes while preserving 16:9");
        Check(File.ReadAllBytes(fixture).SequenceEqual(original), "preparing a picture leaves its source file unchanged");
        var tall = BitmapSource.Create(20, 4000, 96, 96, PixelFormats.Bgr32, null, new byte[20 * 4000 * 4], 20 * 4);
        var tallImage = AiImageAttachment.FromBitmap(tall);
        Check(tallImage.Width == 8 && tallImage.Height == 1600, "very tall pictures are limited by their long edge");
        var noise = new byte[1600 * 1600 * 4]; new Random(42).NextBytes(noise);
        var noisy = AiImageAttachment.FromBitmap(BitmapSource.Create(1600, 1600, 96, 96, PixelFormats.Bgra32, null, noise, 1600 * 4));
        Check(noisy.ByteCount <= AiLimits.ImageBytes && noisy.Width < 1600, "high-entropy PNG is reduced again to meet the independent byte budget");
        var overPixels = BitmapSource.Create(8001, 5000, 96, 96, PixelFormats.BlackWhite, BitmapPalettes.BlackAndWhite,
            new byte[((8001 + 7) / 8) * 5000], (8001 + 7) / 8);
        Reject<InvalidDataException>(() => AiImageAttachment.FromBitmap(overPixels), "over-40-megapixel clipboard image is rejected before conversion");

        using (var server = new MockServer())
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            Check(!vm.SendCommand.CanExecute(null) && Await(vm.SelectImageAsync(fixture)) && vm.SendCommand.CanExecute(null) && server.Requests.IsEmpty,
                "choosing a picture enables image-only sending but sends no network request");
            var pending = vm.Image;
            var bad = Path.Combine(Output, "bad-image.png"); File.WriteAllText(bad, "not an image");
            var large = Path.Combine(Output, "large-image.png"); using (var file = File.Create(large)) file.SetLength(32 * 1024 * 1024 + 1);
            foreach (var path in new[] { bad, large, Path.Combine(Output, "missing-picture.png") })
                Check(!Await(vm.SelectImageAsync(path)) && ReferenceEquals(vm.Image, pending) && vm.CanEditInput && server.Requests.IsEmpty,
                    "invalid, oversized or missing picture preserves the previous attachment: " + Path.GetFileName(path));
            Check(!Await(vm.PasteImageAsync(overPixels)) && ReferenceEquals(vm.Image, pending), "oversized clipboard image fails safely and keeps the draft");
            vm.RemoveImageCommand.Execute(null);
            Check(!vm.HasImage && !vm.SendCommand.CanExecute(null) && server.Requests.IsEmpty, "removing a draft attachment restores the empty composer without uploading");
        }

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var server = new MockServer(Route("answer"), Sse(new(Delta("识别结果：这是一张测试画面。")),
                   new(Delta("\n画面依据：标题写有 YUME VISION TEST。无法据此确定真实作品。") + Delta(finish: "stop") + Done, Release: release.Task)), Route("intro")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint) with { AiVisionModel = "vision-test-model" });
            Check(Await(vm.SelectImageAsync(fixture)), "local image can be prepared on the worker thread");
            var page = new AiAssistantPage { DataContext = vm };
            var host = new Window { Content = page };
            host.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            var root = (FrameworkElement)page.Content;
            Render(root, "ai-vision-preview.png", 900, 650);
            Render(root, "ai-vision-preview-narrow.png", 680, 540);
            var preview = (Border)page.FindName("AttachmentPreview");
            var scroll = (ScrollViewer)page.FindName("ConversationScroll");
            var send = Descendants(root).OfType<Button>().Single(x => Equals(x.Content, "发送"));
            Check(preview.ActualHeight >= 76 && scroll.ActualHeight >= 140 && send.IsEnabled && send.TranslatePoint(new Point(0, send.ActualHeight), root).Y <= 541,
                "narrow composer keeps preview, conversation and send button within the page");
            ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings { NightMode = true });
            Render(root, "ai-vision-preview-night.png", 900, 650);
            ThemePalette.Apply(System.Windows.Application.Current.Resources, new AppSettings());
            var imageId = vm.Image!.Id;
            vm.SendCommand.Execute(null);
            PumpUntil(() => vm.Conversation.Last().Content.Length > 0 || !vm.IsBusy);
            Check(vm.IsBusy && vm.Conversation.Last().Content.Contains("测试画面") && !vm.CanEditInput && !vm.RemoveImageCommand.CanExecute(null),
                "vision answer streams before completion while attachment controls remain locked");
            Check(!Await(vm.SelectImageAsync(fixture)), "an active vision request cannot have its attachment replaced");
            vm.RemoveImageCommand.Execute(null); vm.SendCommand.Execute(null);
            Check(vm.Image?.Id == imageId && server.Requests.Count == 2, "double-send and direct remove do not alter an active vision task");
            Render(root, "ai-vision-streaming.png", 900, 650);
            release.SetResult(); PumpUntil(() => !vm.IsBusy);
            Check(!vm.HasImage && vm.Query.Length == 0 && vm.Conversation[^2].ImagePreview is not null && vm.Conversation.Last().State == "已完成",
                "successful vision turn clears the payload draft and retains a visible thumbnail");
            var requests = server.Requests.ToArray();
            foreach (var request in requests.Take(1))
            {
                using var doc = JsonDocument.Parse(request);
                var content = Messages(request).Last().GetProperty("content");
                Check(doc.RootElement.GetProperty("model").GetString() == "vision-test-model" && content.ValueKind == JsonValueKind.Array &&
                      content[0].GetProperty("type").GetString() == "text" && content[0].GetProperty("text").GetString()!.Contains("识别") &&
                      content[1].GetProperty("type").GetString() == "image_url", "visual routing and evidence extraction use the vision model and text-plus-image content");
            }
            using var answerPayload = JsonDocument.Parse(requests[1]);
            Check(answerPayload.RootElement.GetProperty("model").GetString() == "test-model" && !requests[1].Contains("data:image/") &&
                Messages(requests[1]).Last().GetProperty("content").GetString()!.Contains("视觉工具结果"), "answer model reuses visual evidence without uploading the picture again");
            var url = Messages(requests[0]).Last().GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString()!;
            var bytes = Convert.FromBase64String(url["data:image/png;base64,".Length..]);
            using var stream = new MemoryStream(bytes);
            var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Check(decoded.PixelWidth == 1600 && decoded.PixelHeight == 900 && Encoding.UTF8.GetString(original).Contains("VISION_PRIVATE_METADATA") &&
                  !Encoding.UTF8.GetString(bytes).Contains("VISION_PRIVATE_METADATA") && !requests[0].Contains("vision-fixture.png"),
                "wire payload is a valid resized PNG without source metadata or a local filename");
            Send(vm, "你能做什么");
            using var follow = JsonDocument.Parse(server.Requests.Last());
            Check(follow.RootElement.GetProperty("model").GetString() == "test-model" &&
                  Messages(server.Requests.Last()).All(x => x.GetProperty("content").ValueKind == JsonValueKind.String) &&
                  server.Requests.Last().Contains("data:image/") == false && Messages(server.Requests.Last()).Any(x => x.GetProperty("content").GetString()!.Contains("历史不重发图片")),
                "text follow-up returns to the text model and carries only completed textual context, never old image bytes");
            Render(root, "ai-vision-complete.png", 900, 650);
            host.Close();
        }

        using (var server = new MockServer(new Reply(400, "VISION_PRIVATE_SERVER_ERROR"), Route("answer"),
                   Sse(new StreamPart(Delta("重试识别完成") + Delta(finish: "stop") + Done))))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Await(vm.SelectImageAsync(fixture)); Send(vm);
            var ids = vm.Conversation.Select(x => x.Id).ToArray(); var pending = vm.Image;
            Check(vm.HasImage && vm.Query.Length == 0 && vm.RetryCommand.CanExecute(null) && vm.Status.Contains("识图模型") && !vm.Status.Contains("PRIVATE"),
                "unsupported image request gives an actionable error and retains image-only draft for retry");
            vm.UpdateSettings(AiSettings(server.Endpoint) with { AiVisionModel = "fixed-vision-model" });
            vm.RetryCommand.Execute(null); PumpUntil(() => !vm.IsBusy);
            using var retry = JsonDocument.Parse(server.Requests.ElementAt(1));
            Check(ids.SequenceEqual(vm.Conversation.Select(x => x.Id)) && vm.Conversation.Last().Content == "重试识别完成" &&
                  retry.RootElement.GetProperty("model").GetString() == "fixed-vision-model" && !vm.HasImage && pending is not null,
                "retry after fixing model settings reuses the original bubbles and attachment");
        }

        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var server = new MockServer(Route("answer"), Sse(new(Delta("未完成的图片判断")), new(Delta("不该继续") + Done, Release: late.Task)),
                   Route("out_of_scope"), Route("intro")))
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint)); Await(vm.SelectImageAsync(fixture)); vm.Query = "这是什么游戏？";
            vm.SendCommand.Execute(null); PumpUntil(() => vm.Conversation.Last().Content.Length > 0 || !vm.IsBusy);
            vm.Cancel(); PumpUntil(() => !vm.IsBusy); late.SetResult();
            var oldImage = vm.Image!.Id; var oldUser = vm.Conversation[^2]; var count = vm.Conversation.Count;
            Check(vm.HasImage && vm.Query == "这是什么游戏？" && vm.RetryCommand.CanExecute(null) && vm.Conversation.Last().Content == "未完成的图片判断",
                "stopping a vision stream preserves question, image and partial answer");
            Await(vm.PasteImageAsync(tall));
            Check(!vm.RetryCommand.CanExecute(null) && vm.Image!.Id != oldImage, "same question with a different picture is a new task, not a retry");
            Send(vm);
            Check(vm.Conversation.Count == count + 2 && oldUser.ImageId == oldImage && vm.Conversation.Last().Content.Contains("只处理") && server.Requests.Count == 3,
                "replacement image gets separate bubbles and out-of-scope routing stops after one model call");
            Send(vm, "你好");
            Check(Messages(server.Requests.Last()).Length == 2, "incomplete vision and unrelated-image turns never enter successful history");
        }

        using (var server = new MockServer())
        {
            var vm = new AiAssistantViewModel(AiSettings(server.Endpoint));
            var page = new AiAssistantPage { DataContext = vm };
            var editor = (TextBox)page.FindName("QueryEditor");
            var bitmapData = new DataObject(DataFormats.Bitmap, image.Preview);
            var paste = new DataObjectPastingEventArgs(bitmapData, false, DataFormats.Bitmap);
            editor.RaiseEvent(paste); PumpUntil(() => !vm.IsPreparingImage);
            Check(paste.CommandCancelled && vm.HasImage && vm.Image!.Name == "剪贴板截图" && server.Requests.IsEmpty,
                "WPF image paste prepares a local attachment without changing the system clipboard or uploading");
            var textData = new DataObject(DataFormats.UnicodeText, "普通文字");
            var textPaste = new DataObjectPastingEventArgs(textData, false, DataFormats.UnicodeText);
            editor.RaiseEvent(textPaste);
            Check(!textPaste.CommandCancelled, "normal text paste retains the native TextBox behavior");
        }

        AppSettings? saved = null;
        var settings = new SettingsViewModel(new AppSettings(), x => saved = x.Normalize());
        settings.AiVisionModel = new string('x', 201); settings.ConfirmCommand.Execute(null);
        Check(saved is null && settings.ErrorMessage.Contains("模型名称"), "invalid optional vision model cannot be saved");
        settings.AiVisionModel = "  optional-vision  "; settings.ConfirmCommand.Execute(null);
        var store = new AppSettingsStore(Path.Combine(Output, "vision-settings.json")); store.Save(saved!);
        Check(store.Load().AiVisionModel == "optional-vision" && JsonSerializer.Deserialize<AppSettings>("{}")!.Normalize().AiVisionModel == "",
            "optional vision model persists and old configuration defaults to the existing text model");
        settings.SelectedSection = "AI 检索";
        var settingsPage = new SettingsPage { DataContext = settings };
        Render((FrameworkElement)settingsPage.Content, "ai-vision-settings.png", 900, 650);
        Check(!Directory.GetFiles(AppLog.DirectoryPath).Any(p => File.ReadAllText(p).Contains("data:image/") || File.ReadAllText(p).Contains("VISION_PRIVATE")),
            "diagnostic logs contain no image payload, private metadata or raw service errors");
    }

    private static string VisionFixture()
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.AliceBlue, null, new Rect(0, 0, 1920, 1080));
            dc.DrawEllipse(Brushes.LightSteelBlue, null, new Point(1450, 460), 270, 270);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(90, 730, 1740, 250), 40, 40);
            dc.DrawText(new FormattedText("YUME VISION TEST", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 86, Brushes.DarkSlateGray, 1), new Point(120, 200));
            dc.DrawText(new FormattedText("Synthetic fixture - no actual game", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 48, Brushes.DarkSlateGray, 1), new Point(145, 815));
        }
        var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var metadata = new BitmapMetadata("png"); metadata.SetQuery("/tEXt/{str=Comment}", "VISION_PRIVATE_METADATA");
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        var path = Path.Combine(Output, "vision-fixture.png"); using var file = File.Create(path); png.Save(file); return path;
    }
}
