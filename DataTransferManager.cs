namespace BlitzkriegWPF;

/// <summary>Подключает сохранение мода через пункт меню «Импорт».</summary>
internal sealed class DataTransferManager
{
    public LinkSaveResult Import(string file) => SaveSyncManager.LinkSave(file);
}
