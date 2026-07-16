using System.Text.Json;
using Ezvpn.Core;

namespace Ezvpn.App.Services;

/// <summary>
/// Persists <see cref="TunnelProfile"/> records as JSON files under
/// <c>%ProgramData%\ezvpn\profiles\</c> (the elevated app can write there; it
/// matches the CLI's config dir). One file per profile, named by id. The auth
/// token is NOT stored here — see <see cref="TokenStore"/>.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _dir;

    public ProfileStore()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ezvpn",
            "profiles");
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(Guid id) => Path.Combine(_dir, id.ToString("N") + ".json");

    public IReadOnlyList<TunnelProfile> LoadAll()
    {
        var result = new List<TunnelProfile>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<TunnelProfile>(File.ReadAllText(file));
                if (profile is not null && profile.Id != Guid.Empty)
                {
                    result.Add(profile);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Skip a corrupt/locked profile file rather than failing startup.
            }
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Save(TunnelProfile profile)
    {
        File.WriteAllText(PathFor(profile.Id), JsonSerializer.Serialize(profile, Options));
    }

    public void Delete(Guid id)
    {
        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
