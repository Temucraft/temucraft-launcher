using Avalonia.Threading;
using Launcher.Core;

namespace Launcher.App;

/// <summary>
/// Switching between choices of a mod that are already on this PC (Classic and HD after HD was installed once): the
/// files of the old choice are kept aside, the new one's come back, no download and no UPDATE needed.
/// </summary>
public partial class MainWindow
{
    /// <summary>The files a choice of a group installs here (for the ticked languages).</summary>
    List<PlannedFile> OptionFiles(OptionGroup group, string option)
    {
        if (_manifest is null) return [];
        Dictionary<string, string> options = new(_settings.Options) { [group.Name] = option };
        InstallChoice choice = Choice() with { Options = options };
        return [.. UpdatePlanner.Wanted(_manifest, choice).Where(p => p.File.Group == group.Name
                    && string.Equals(p.File.Option, option, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>True when every file of the choice is on this PC: in place or kept aside from before.</summary>
    bool OptionOnDisk(OptionGroup group, string option)
    {
        if (!GameFolder.LooksLikeClient(Folder)) return false;
        List<PlannedFile> files = OptionFiles(group, option);
        return files.All(p =>   // a choice with no files of its own (Classic, Off) is always "on this PC"
            (File.Exists(p.Target) && new FileInfo(p.Target).Length == p.File.Size) || KeptFiles.Has(Folder, p.File.Sha256));
    }

    /// <summary>The group's choices that are on this PC (for "Installed: ..." on the card).</summary>
    List<string> OptionsOnDisk(OptionGroup group) => [.. group.Options.Where(o => OptionOnDisk(group, o))];

    /// <summary>Puts the picked choice of the group in place from this PC: the other choices' files go aside (kept),
    /// the picked one's come back. True when nothing is left to download.</summary>
    async Task<bool> SwapOptionAsync(OptionGroup group, string option)
    {
        string folder = Folder;
        if (_manifest is null || !GameFolder.LooksLikeClient(folder)) return false;
        if (!await MakeSureGameClosedAsync(folder)) return false;
        Manifest manifest = _manifest;
        InstallChoice choice = Choice();
        HashSet<string> ofGroup = manifest.Files.Where(f => f.Group == group.Name).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<PlannedFile> wanted = OptionFiles(group, option);
        try
        {
            return await Task.Run(async () =>
            {
                HashCache cache = HashCache.Load(_cacheFile);
                foreach (string kept in await KeptFiles.KeepAsync(folder, UpdatePlanner.Unchosen(manifest, choice).Where(ofGroup.Contains), cache))
                    Logger.Info($"Kept {kept} aside ({group.Name}: {option})", toWindow: false);
                List<PlannedFile> missing = [.. wanted.Where(p => !(File.Exists(p.Target) && new FileInfo(p.Target).Length == p.File.Size))];
                List<PlannedFile> left = KeptFiles.Restore(folder, missing, cache, line => Logger.Info(line, toWindow: false));
                cache.Save();
                return left.Count == 0;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Logger.Warning($"Couldn't switch {group.Name} to {option} (is the game running?)", error);
            return false;
        }
    }
}
