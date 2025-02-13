﻿using RandomizerTMF.Logic;
using RandomizerTMF.Models;
using RandomizerTMF.Views;
using ReactiveUI;
using System.Collections.ObjectModel;

namespace RandomizerTMF.ViewModels;

public class HistoryModuleWindowViewModel : WindowViewModelBase
{
    private ObservableCollection<PlayedMapModel> playedMaps = new();

    public ObservableCollection<PlayedMapModel> PlayedMaps
    {
        get => playedMaps;
        set => this.RaiseAndSetIfChanged(ref playedMaps, value); 
    }

    public bool HasFinishedMaps => PlayedMaps.Count > 0;

    public HistoryModuleWindowViewModel()
    {
        RandomizerEngine.MedalUpdate += RandomizerPlayedMapUpdate;
        RandomizerEngine.MapSkip += RandomizerPlayedMapUpdate;
    }

    private void RandomizerPlayedMapUpdate()
    {
        PlayedMaps = new(EnumerateCurrentSessionMaps().OrderByDescending(x => x.Map.ReceivedAt));
        this.RaisePropertyChanged(nameof(HasFinishedMaps));
    }

    private IEnumerable<PlayedMapModel> EnumerateCurrentSessionMaps()
    {
        foreach (var map in RandomizerEngine.CurrentSessionAuthorMaps)
        {
            yield return new PlayedMapModel(map.Value, EResult.AuthorMedal);
        }

        foreach (var map in RandomizerEngine.CurrentSessionGoldMaps)
        {
            yield return new PlayedMapModel(map.Value, EResult.GoldMedal);
        }

        foreach (var map in RandomizerEngine.CurrentSessionSkippedMaps)
        {
            yield return new PlayedMapModel(map.Value, EResult.Skipped);
        }
    }

    public void MapDoubleClick(PlayedMapModel? selectedItem)
    {
        if (selectedItem is null)
        {
            return;
        }

        OpenDialog<SessionMapWindow>(window => new SessionMapViewModel(selectedItem)
        {
            Window = window
        });
    }
}
