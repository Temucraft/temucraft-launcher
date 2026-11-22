using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// The Screens page: what the player sees before entering the world. Two views behind the buttons at the top: the login
/// screen backgrounds and the character screen scenes. Save writes both (loginui.lua and TemucraftScenes.lua).
/// </summary>
public partial class MainWindow
{
    void BuildScreens()
    {
        ScreensLoginList.Children.Clear();
        ScreensCharList.Children.Clear();
        BuildLoginScenes(ScreensLoginList);
        BuildLoginMusic(ScreensLoginList);
        BuildCharacterScenes(ScreensCharList);
        BuildCharacterMusic(ScreensCharList);
        ShowScreensView(login: true);
    }

    void OnScreensTab(object? sender, RoutedEventArgs e) => ShowScreensView(login: sender == ScreensLoginTab);

    void ShowScreensView(bool login)
    {
        ScreensLoginTab.IsChecked = login;
        ScreensCharTab.IsChecked = !login;
        ScreensLoginList.IsVisible = login;
        ScreensCharList.IsVisible = !login;
    }

    /// <summary>Save: the ticked login backgrounds and the character screen picks.</summary>
    void OnSaveScreens(object? sender, RoutedEventArgs e)
    {
        try
        {
            List<string> scenes = _sceneBoxes.Where(b => b.Value.IsChecked == true).Select(b => b.Key).ToList();
            if (_sceneBoxes.Count > 0 && scenes.Count > 0
                && !LoginScreens.Read(Folder).Where(x => x.On).Select(x => x.Code).Order().SequenceEqual(scenes.Order()))
            {
                _settings.LoginScenes = scenes;
                SaveSettings();
                ApplyLoginScenes(Folder);
            }
            SaveCharacterScenes();
            Status("Screens saved.");
        }
        catch (Exception error)
        {
            Logger.Error("Saving the screens", error);
            Status($"Couldn't save the screens: {error.Message} (is the game running?)");
        }
    }
}
