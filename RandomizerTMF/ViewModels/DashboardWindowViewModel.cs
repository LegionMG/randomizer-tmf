﻿using Avalonia.Controls;
using RandomizerTMF.Logic;
using RandomizerTMF.Models;
using RandomizerTMF.Views;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.ComponentModel.Design;
using System.Diagnostics;

namespace RandomizerTMF.ViewModels;

public class DashboardWindowViewModel : WindowViewModelBase
{
    private ObservableCollection<AutosaveModel> autosaves = new();

    public TopBarViewModel TopBarViewModel { get; set; }
    public RequestRulesControlViewModel RequestRulesControlViewModel { get; set; }

    public string? GameDirectory => RandomizerEngine.Config.GameDirectory;

    public ObservableCollection<AutosaveModel> Autosaves
    {
        get => autosaves;
        private set => this.RaiseAndSetIfChanged(ref autosaves, value);
    }

    public bool HasAutosavesScanned => RandomizerEngine.HasAutosavesScanned;
    public int AutosaveScanCount => RandomizerEngine.Autosaves.Count;

    public DashboardWindowViewModel()
    {
        TopBarViewModel = new();
        TopBarViewModel.CloseClick += CloseClick;
        TopBarViewModel.MinimizeClick += MinimizeClick;

        RequestRulesControlViewModel = new();
    }

    protected internal override void OnInit()
    {
        Window.Opened += Opened;
    }

    private async void Opened(object? sender, EventArgs e)
    {
        var anythingChanged = await ScanAutosavesAsync();

        if (anythingChanged)
        {
            await UpdateAutosavesWithFullParseAsync();
        }
    }

    private async Task<bool> ScanAutosavesAsync()
    {
        var cts = new CancellationTokenSource();

        var anythingChanged = Task.Run(RandomizerEngine.ScanAutosaves);

        await Task.WhenAny(anythingChanged, Task.Run(async () =>
        {
            while (true)
            {
                Autosaves = new(GetAutosaveModels());
                this.RaisePropertyChanged(nameof(AutosaveScanCount));
                await Task.Delay(20, cts.Token);
            }
        }));

        cts.Cancel();
        
        Autosaves = new(GetAutosaveModels());

        this.RaisePropertyChanged(nameof(HasAutosavesScanned));

        return anythingChanged.Result;
    }

    private async Task UpdateAutosavesWithFullParseAsync()
    {
        var cts = new CancellationTokenSource();

        await Task.WhenAny(Task.Run(RandomizerEngine.ScanDetailsFromAutosaves), Task.Run(async () =>
        {
            while (true)
            {
                Autosaves = new(GetAutosaveModels());
                await Task.Delay(20, cts.Token);
            }
        }));

        cts.Cancel();

        Autosaves = new(GetAutosaveModels());
    }

    private static IEnumerable<AutosaveModel> GetAutosaveModels()
    {
        return RandomizerEngine.AutosaveDetails.Select(x => new AutosaveModel(x.Key, x.Value)).OrderBy(x => x.Autosave.MapName);
    }

    public void CloseClick()
    {
        RandomizerEngine.Exit();
    }

    public void MinimizeClick()
    {
        Window.WindowState = WindowState.Minimized;
    }

    public void ChangeClick()
    {
        SwitchWindowTo<MainWindow, MainWindowViewModel>();
    }

    public void StartModulesClick()
    {
        try
        {
            RandomizerEngine.ValidateRules();
        }
        catch (Exception ex)
        {
            OpenMessageBox("Validation problem", ex.Message);
            return;
        }

        App.Modules = new Window[]
        {
            OpenModule<ControlModuleWindow, ControlModuleWindowViewModel>(RandomizerEngine.Config.Modules.Control),
            OpenModule<StatusModuleWindow, StatusModuleWindowViewModel>(RandomizerEngine.Config.Modules.Status),
            OpenModule<ProgressModuleWindow, ProgressModuleWindowViewModel>(RandomizerEngine.Config.Modules.Progress),
            OpenModule<HistoryModuleWindow, HistoryModuleWindowViewModel>(RandomizerEngine.Config.Modules.History)
        };

        Window.Close();
    }

    private static TWindow OpenModule<TWindow, TViewModel>(ModuleConfig config)
        where TWindow : Window, new()
        where TViewModel : WindowViewModelBase, new()
    {
        var window = OpenWindow<TWindow, TViewModel>();

        if (config.X < 0)
        {
            config.X += window.Screens.Primary.WorkingArea.Width - config.Width;
        }

        if (config.Y < 0)
        {
            config.Y += window.Screens.Primary.WorkingArea.Height - config.Height;
        }

        window.Position = new(config.X, config.Y);
        window.Width = config.Width;
        window.Height = config.Height;

        return window;
    }

    public void AutosaveDoubleClick(int selectedIndex)
    {
        if (selectedIndex < 0)
        {
            return;
        }

        var autosaveModel = Autosaves[selectedIndex];

        if (!RandomizerEngine.AutosavePaths.TryGetValue(autosaveModel.MapUid, out string? fileName))
        {
            return;
        }

        OpenDialog<AutosaveDetailsWindow>(window => new AutosaveDetailsWindowViewModel(autosaveModel, fileName)
        {
            Window = window
        });
    }

    public void OpenDownloadedMapsFolderClick()
    {
        if (RandomizerEngine.DownloadedDirectoryPath is not null)
        {
            OpenFolder(RandomizerEngine.DownloadedDirectoryPath + Path.DirectorySeparatorChar);
        }
    }

    public void OpenSessionsFolderClick()
    {
        if (RandomizerEngine.SessionsDirectoryPath is not null)
        {
            OpenFolder(RandomizerEngine.SessionsDirectoryPath + Path.DirectorySeparatorChar);
        }
    }

    private static void OpenFolder(string folderPath)
    {
        Process.Start(new ProcessStartInfo()
        {
            FileName = folderPath,
            UseShellExecute = true,
            Verb = "open"
        });
    }
}
