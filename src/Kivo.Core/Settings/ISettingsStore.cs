using Kivo.Core.Models;

namespace Kivo.Core.Settings;

public interface ISettingsStore
{
    AppSettingsData Load();

    void Save(AppSettingsData data);
}
