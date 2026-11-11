using Kivo.Core.Models;
using Kivo.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kivo.Desktop;

public sealed partial class MainWindow
{
    private sealed record CaptureSource(string Id, string Name, string Description, string Help);
    private static readonly CaptureSource[] Sources =
    [
        new("nvidia", "NVIDIA ShadowPlay / NVIDIA App", "Game clips and Instant Replay recordings.", "Check Alt+Z → Gallery or the overlay's recording storage setting. Locations vary by version and game."),
        new("amd", "AMD Software: Adrenalin Edition / ReLive", "Record & Stream and instant replay clips.", "Check Record & Stream → Settings → Media Save Location. Older ReLive versions call this Save Folder."),
        new("intel", "Intel capture tools", "Recordings from Intel capture software.", "Check the capture tool's output settings. Intel versions differ; Kivo does not assume a universal default."),
        new("xbox", "Xbox Game Bar", "Windows game captures and recorded moments.", "Usually Videos → Captures. Windows allows moving this folder; confirm the location in Gaming → Captures."),
        new("obs", "OBS Studio", "Completed MP4 recordings from OBS.", "Check Settings → Output → Recording Path. Kivo shares MP4 only; MKV recordings will not appear."),
        new("custom", "Custom folder", "Any readable folder containing MP4 recordings.", "Select the folder where your completed MP4 files are saved. Subfolders are included."),
    ];

    private static string SourceName(string id) => Sources.FirstOrDefault(source => source.Id == id)?.Name ?? "Custom folder";

    private void ShowRecordings()
    {
        var list = Stack();
        var summary = Text("", 14, true);
        var page = Stack(Heading("Recordings", "Choose the folders Kivo watches. Your files stay in their original location."),
            summary, list, ActionButton("Add recording folder", () => EditDirectoryAsync(null), true));
        page.Spacing = 20;
        PageHost.Content = page;
        string? previous = null;
        _refreshPage = () =>
        {
            var directories = _app.Controller.RecordingDirectories;
            var errors = _app.Controller.DirectoryErrors;
            summary.Text = $"{_app.Controller.RecordingCount:N0} completed recordings discovered. Only metadata is held in memory.";
            var signature = _app.Controller.Status.State + string.Join('|', directories.Select(directory => $"{directory.Source}:{directory.Path}:{errors.GetValueOrDefault(directory.Path)}"));
            if (signature == previous) return;
            previous = signature;
            list.Children.Clear();
            if (directories.Count == 0) list.Children.Add(Card(Stack(Text("No recording folders yet", 18), Text("Add a folder to start your library. Empty folders are welcome.", 14, true))));
            foreach (var directory in directories)
            {
                var error = errors.GetValueOrDefault(directory.Path);
                var row = Stack(Text(SourceName(directory.Source), 17), Technical(directory.Path),
                    Text(error ?? (_app.Controller.Status.State == Kivo.Windows.Services.SharingState.Running
                        ? "Watching this folder and its subfolders for completed MP4 files."
                        : "Folder configured. Start sharing from Overview to discover recordings."), 13, error is null),
                    ActionButton("Edit folder", () => EditDirectoryAsync(directory)),
                    ActionButton("Remove folder", () => RemoveDirectoryAsync(directory)));
                list.Children.Add(Card(row));
            }
        };
        _refreshPage();
    }

    private async Task RemoveDirectoryAsync(RecordingDirectory directory)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = "Stop watching this folder?",
            Content = Stack(Technical(directory.Path), Text("Recordings on disk are kept. This folder's clips will no longer be available to paired devices.")),
            PrimaryButtonText = "Remove folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var remaining = _app.Controller.RecordingDirectories.Where(item => !string.Equals(item.Path, directory.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!await Task.Run(() => _app.Controller.ApplyRecordingDirectoriesAsync(remaining))) ShowError(_app.Controller.ConfigurationError ?? "The folder configuration could not be saved.");
    }

    private static ComboBox SourcePicker(string selected = "custom")
    {
        var picker = new ComboBox { Header = "Recording software", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var source in Sources) picker.Items.Add(new ComboBoxItem { Content = source.Name, Tag = source.Id });
        picker.SelectedIndex = Array.FindIndex(Sources, source => source.Id == selected);
        return picker;
    }

    private async Task EditDirectoryAsync(RecordingDirectory? existing)
    {
        var source = SourcePicker(existing?.Source ?? "custom");
        var path = new TextBox { Header = "Recording directory", Text = existing?.Path ?? "", PlaceholderText = @"C:\Users\you\Videos\Captures", TextWrapping = TextWrapping.Wrap };
        var validation = Text("Choose any readable folder. Empty folders can be added.", 13, true);
        var browse = new Button { Content = "Browse folders…" };
        var content = Stack(source, path, browse, validation);
        browse.Click += async (_, _) =>
        {
            try
            {
                var chosen = await PickFolderAsync();
                if (chosen is not null) path.Text = chosen;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                validation.Text = "The folder picker could not open. Enter a folder path directly.";
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = existing is null ? "Add recording folder" : "Edit recording folder",
            Content = content,
            PrimaryButtonText = "Save folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var chosenPath = path.Text;
                var result = await Task.Run(() => _app.Validator.Validate(chosenPath));
                if (!result.IsValid) { args.Cancel = true; validation.Text = result.Error; return; }
                var directories = _app.Controller.RecordingDirectories.ToList();
                if (existing is not null) directories.RemoveAll(item => string.Equals(item.Path, existing.Path, StringComparison.OrdinalIgnoreCase));
                directories.Add(new RecordingDirectory { Path = chosenPath, Source = (string)((ComboBoxItem)source.SelectedItem).Tag });
                if (!await Task.Run(() => _app.Controller.ApplyRecordingDirectoriesAsync(directories)))
                {
                    args.Cancel = true;
                    validation.Text = _app.Controller.ConfigurationError ?? "Settings could not be saved. Retry after checking permissions.";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                args.Cancel = true;
                validation.Text = "The folder could not be saved. Check the path and read permission, then retry.";
            }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new global::Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private void ShowSourceStep()
    {
        _pairingTimer.Stop();
        _refreshPage = null;
        Navigation.IsPaneVisible = false;
        var options = new RadioButtons { MaxColumns = 2 };
        options.SizeChanged += (_, args) =>
        {
            options.MaxColumns = args.NewSize.Width >= 680 ? 2 : 1;
            foreach (var option in options.Items.Cast<RadioButton>())
                ((StackPanel)option.Content).MaxWidth = Math.Max(180, args.NewSize.Width / options.MaxColumns - 72);
        };
        foreach (var source in Sources)
        {
            var option = new RadioButton { Content = Stack(Text(source.Name, 16), Text(source.Description, 13, true)), Tag = source.Id };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(option, $"{source.Name}. {source.Description}");
            options.Items.Add(option);
        }
        var next = ActionButton("Continue", () =>
        {
            var id = (string)((RadioButton)options.SelectedItem).Tag;
            ShowDirectoryStep(Sources.First(source => source.Id == id));
            return Task.CompletedTask;
        }, true);
        next.IsEnabled = false;
        options.SelectionChanged += (_, _) => next.IsEnabled = options.SelectedItem is not null;
        PageHost.Content = Stack(Text("GET STARTED · 1 OF 2", 11, true),
            Heading("Where do your recordings come from?", "Choose your recording software. Kivo will help you locate your clips."),
            Card(options), next,
            ActionButton("Back to Kivo", () => { Navigation.IsPaneVisible = true; Navigate("overview"); return Task.CompletedTask; }));
    }

    private void ShowDirectoryStep(CaptureSource source)
    {
        var path = new TextBox { Header = "Recording directory", PlaceholderText = "Choose a folder or paste its full path", TextWrapping = TextWrapping.Wrap };
        var help = Text("", 13, true);
        var suggestions = new ComboBox { Header = "Existing folders to check", PlaceholderText = "Checking local folders…", HorizontalAlignment = HorizontalAlignment.Stretch };
        suggestions.SelectionChanged += (_, _) => { if (suggestions.SelectedItem is string candidate) path.Text = candidate; };
        var browse = ActionButton("Browse folders…", async () =>
        {
            var chosen = await PickFolderAsync();
            if (chosen is not null) path.Text = chosen;
        });
        var save = ActionButton("Use this folder", async () =>
        {
            var selectedPath = path.Text;
            var result = await Task.Run(() => _app.Validator.Validate(selectedPath));
            if (!result.IsValid) { help.Text = result.Error; return; }
            // Restarting onboarding adds a source; it never discards existing folders.
            var directories = _app.Controller.RecordingDirectories.ToList();
            var same = directories.FirstOrDefault(directory => string.Equals(Path.TrimEndingDirectorySeparator(directory.Path), Path.TrimEndingDirectorySeparator(selectedPath), StringComparison.OrdinalIgnoreCase));
            if (same is not null) directories.Remove(same);
            directories.Add(new RecordingDirectory { Path = selectedPath, Source = source.Id });
            if (!await Task.Run(() => _app.Controller.ApplyRecordingDirectoriesAsync(directories, completeOnboarding: true)))
            { help.Text = _app.Controller.ConfigurationError; return; }
            Navigation.IsPaneVisible = true;
            Navigation.SelectedItem = Navigation.MenuItems[2];
            Navigate("devices");
        }, true);
        path.TextChanged += async (_, _) =>
        {
            var current = path.Text;
            var message = await Task.Run(() =>
            {
                var result = _app.Validator.Validate(current);
                if (!result.IsValid) return result.Error;
                try { return Directory.EnumerateFileSystemEntries(current).Any() ? "Folder is readable. Kivo will scan MP4 files and subfolders after you confirm." : "This folder is empty. New completed MP4 recordings will appear automatically."; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "The folder became unavailable. Check the drive and choose it again."; }
            });
            if (path.Text == current) help.Text = message;
        };
        PageHost.Content = Stack(Text("GET STARTED · 2 OF 2", 11, true), Heading("Choose your recording folder", source.Name),
            Card(Stack(Text(source.Help, 14, true), suggestions, path, browse, help,
                Text("Confirm the location before continuing. Kivo reads completed MP4 files and never changes your capture settings.", 13, true))),
            save, ActionButton("Back", () => { ShowSourceStep(); return Task.CompletedTask; }));
        _ = LoadSuggestionsAsync();

        async Task LoadSuggestionsAsync()
        {
            var candidates = await Task.Run(() => ExistingSuggestions(source.Id));
            foreach (var candidate in candidates) suggestions.Items.Add(candidate);
            suggestions.PlaceholderText = candidates.Length == 0 ? "No likely folder found. Browse to your recordings." : "Select a candidate, then confirm below";
        }
    }

    private string[] ExistingSuggestions(string source)
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var candidates = source switch
        {
            "xbox" => new[] { Path.Combine(videos, "Captures"), videos },
            "amd" => new[] { Path.Combine(videos, "Radeon ReLive"), videos },
            "nvidia" => new[] { Path.Combine(videos, "NVIDIA"), Path.Combine(videos, "Desktop"), videos },
            _ => new[] { videos },
        };
        return candidates.Where(path => _app.Validator.Validate(path).IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
