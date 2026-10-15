using System;
using Microsoft.Data.Sqlite;

namespace BlitzkriegWPF;

internal static class LogManager
{
    public static void LogAction(string logMessage, int idLogType)
    {
        try
        {
            // Ошибка записи журнала не должна мешать основной работе. Интеграция должна работать
            // даже если в старой базе нет начальных записей в LogType.
            DBManager.WriteLog(logMessage, idLogType);
        }
        catch
        {
            // Сбой журналирования не должен останавливать синхронизацию сохранений.
        }
    }
}
