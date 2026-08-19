namespace Archivio.App;

public interface IFolderPickerService
{
    string? PickFolder(string? initialDirectory = null);
}
