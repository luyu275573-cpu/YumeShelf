using System.Windows;
using System.IO;
using Microsoft.Win32;
using YumeShelf.Domain;

namespace YumeShelf.Presentation;

public partial class GameEditorWindow : Window
{
    private readonly Game _game;

    public GameEditorWindow(Game game)
    {
        _game = game;
        InitializeComponent();
        TitleBox.Text = game.Title;
        EngineBox.Text = game.Engine;
        YearBox.Text = game.ReleaseYear?.ToString() ?? string.Empty;
        WorkingDirectoryBox.Text = game.WorkingDirectory;
        ArgumentsBox.Text = game.LaunchArguments;
        CoverBox.Text = game.CoverPath ?? string.Empty;
        DescriptionBox.Text = game.Description;
    }

    private void ChooseCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = "选择游戏封面"
        };

        if (dialog.ShowDialog(this) == true) CoverBox.Text = dialog.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            System.Windows.MessageBox.Show(this, "标题不能为空。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            TitleBox.Focus();
            return;
        }

        if (!Directory.Exists(WorkingDirectoryBox.Text))
        {
            System.Windows.MessageBox.Show(this, "工作目录不存在，请重新选择或修改。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            WorkingDirectoryBox.Focus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(CoverBox.Text) && !File.Exists(CoverBox.Text))
        {
            System.Windows.MessageBox.Show(this, "封面文件不存在，请重新选择。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            CoverBox.Focus();
            return;
        }

        int? year = null;
        if (!string.IsNullOrWhiteSpace(YearBox.Text) && !int.TryParse(YearBox.Text, out var parsedYear))
        {
            System.Windows.MessageBox.Show(this, "年份必须是数字。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            YearBox.Focus();
            return;
        }
        else if (int.TryParse(YearBox.Text, out parsedYear))
        {
            year = parsedYear;
        }

        _game.Title = TitleBox.Text.Trim();
        _game.Engine = EngineBox.Text.Trim();
        _game.ReleaseYear = year;
        _game.WorkingDirectory = WorkingDirectoryBox.Text.Trim();
        _game.LaunchArguments = ArgumentsBox.Text;
        _game.CoverPath = string.IsNullOrWhiteSpace(CoverBox.Text) ? null : CoverBox.Text.Trim();
        _game.Description = DescriptionBox.Text.Trim();
        DialogResult = true;
    }
}
