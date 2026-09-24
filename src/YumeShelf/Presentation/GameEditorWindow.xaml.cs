using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using YumeShelf.Common;
using YumeShelf.Domain;

namespace YumeShelf.Presentation;

public partial class GameEditorWindow : Window
{
    private readonly Game _game;
    private readonly Func<string, bool> _isDuplicate;
    private readonly CoverImageConverter _cover = new();
    public GameEditorWindow(Game game, Func<string, bool>? isDuplicate = null, bool isRunning = false)
    {
        _game = game;
        _isDuplicate = isDuplicate ?? (_ => false);
        InitializeComponent();
        TitleBox.Text = game.Title; EngineBox.Text = game.Engine;
        YearBox.Text = game.ReleaseYear?.ToString(CultureInfo.InvariantCulture) ?? "";
        DateBox.Text = game.ReleaseDate; TypeBox.Text = game.GameType;
        ExecutableBox.Text = game.ExecutablePath;
        RelinkButton.IsEnabled = !isRunning;
        WorkingDirectoryBox.Text = game.WorkingDirectory;
        ArgumentsBox.Text = game.LaunchArguments;
        CoverBox.Text = game.CoverPath ?? "";
        DescriptionBox.Text = game.Description;
        UpdatePreview();
    }
    private void ChooseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Windows 游戏程序 (*.exe)|*.exe", CheckFileExists = true, Title = "重新选择游戏启动文件" };
        if (dialog.ShowDialog(this) == true) SelectExecutable(dialog.FileName);
    }
    public bool SelectExecutable(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        { ErrorText.Text = "请选择有效的游戏 EXE 文件。"; return false; }
        if (_isDuplicate(path)) { ErrorText.Text = "该启动文件已属于库中的其他游戏。"; return false; }
        ExecutableBox.Text = Path.GetFullPath(path);
        WorkingDirectoryBox.Text = Path.GetDirectoryName(ExecutableBox.Text)!;
        ErrorText.Text = ""; return true;
    }
    private void ChooseCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif", CheckFileExists = true, Title = "选择游戏封面" };
        if (dialog.ShowDialog(this) == true)
        {
            try { _ = GameImageLoader.Load(dialog.FileName); CoverBox.Text = dialog.FileName; UpdatePreview(); ErrorText.Text = ""; }
            catch (Exception ex) when (GameImageLoader.IsImageError(ex)) { ErrorText.Text = "图片无法读取或过大，请重新选择。"; }
        }
    }
    private void CoverBox_LostFocus(object sender, RoutedEventArgs e) => UpdatePreview();
    private void UpdatePreview() => CoverPreview.Source = _cover.Convert(CoverBox.Text, typeof(ImageSource), null, CultureInfo.CurrentCulture) as ImageSource;
    private void Save_Click(object sender, RoutedEventArgs e) { if (TryApply()) DialogResult = true; }

    public bool TryApply()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(TitleBox.Text)) { ErrorText.Text = "标题不能为空。"; TitleBox.Focus(); return false; }
            if (!Directory.Exists(WorkingDirectoryBox.Text.Trim())) { ErrorText.Text = "工作目录不存在，请重新选择启动文件或修改目录。"; return false; }
            if (!string.Equals(ExecutableBox.Text, _game.ExecutablePath, StringComparison.OrdinalIgnoreCase) && !File.Exists(ExecutableBox.Text))
            { ErrorText.Text = "重新选择的启动文件已不存在。"; return false; }
            if (_isDuplicate(ExecutableBox.Text)) { ErrorText.Text = "这个启动文件已属于另一个游戏。"; return false; }
            if (!string.IsNullOrWhiteSpace(CoverBox.Text)) _ = GameImageLoader.Load(CoverBox.Text.Trim());
            int? year = null;
            if (!string.IsNullOrWhiteSpace(YearBox.Text))
            {
                if (!int.TryParse(YearBox.Text, out var parsed) || parsed < 1900 || parsed > DateTime.Now.Year + 10)
                { ErrorText.Text = "请填写有效的四位发行年份，或留空。"; return false; }
                year = parsed;
            }
            var newExe = Path.GetFullPath(ExecutableBox.Text);
            var releaseDate = DateBox.Text.Trim();
            if (releaseDate.Length > 0)
            {
                if (!DateTime.TryParseExact(releaseDate, ["yyyy", "yyyy-MM", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || date.Year < 1900 || date.Year > 2100 || (year is not null && date.Year != year))
                { ErrorText.Text = "请填写有效的发行日期，并与年份保持一致。"; return false; }
                year = date.Year;
            }
            var changed = !string.Equals(newExe, _game.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            _game.Title = TitleBox.Text.Trim(); _game.Engine = EngineBox.Text.Trim(); _game.ReleaseYear = year;
            _game.ReleaseDate = releaseDate; _game.GameType = TypeBox.Text.Trim();
            _game.ExecutablePath = newExe;
            if (changed) _game.RootPath = Path.GetDirectoryName(newExe)!;
            _game.WorkingDirectory = Path.GetFullPath(WorkingDirectoryBox.Text.Trim());
            _game.LaunchArguments = ArgumentsBox.Text;
            _game.CoverPath = string.IsNullOrWhiteSpace(CoverBox.Text) ? null : Path.GetFullPath(CoverBox.Text.Trim());
            _game.Description = DescriptionBox.Text.Trim();
            _game.RefreshAvailability();
            ErrorText.Text = ""; return true;
        }
        catch (Exception ex) when (GameImageLoader.IsImageError(ex))
        { ErrorText.Text = "路径或封面无效，图片可能已损坏或超过大小限制。请检查后重试。"; return false; }
    }
}
