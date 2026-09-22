using System.Net;
using System.Net.Http;

namespace OpusConverter.Gui;

/// <summary>Turns exceptions into a short explanation the user can act on.</summary>
public static class ErrorText
{
    public static string Describe(Exception e) => e switch
    {
        NoAudioException => "В файле нет звука или он повреждён",
        NotDirectLinkException => "Это страница сайта, а не файл. Нужна прямая ссылка",
        DownloadTooLargeException d => $"Файл больше лимита загрузки ({d.LimitMb} МБ)",
        FileNotFoundException f when f.Message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) => "Не найден ffmpeg",
        FileNotFoundException => "Файл не найден",
        HttpRequestException h => Describe(h),
        TaskCanceledException => "Сервер не ответил вовремя",
        UnauthorizedAccessException => "Нет доступа к файлу или папке",
        IOException io => "Ошибка чтения/записи: " + io.Message,
        _ => e.Message,
    };

    private static string Describe(HttpRequestException e) => e.StatusCode switch
    {
        HttpStatusCode.NotFound => "Ссылка не найдена (404)",
        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => $"Доступ к ссылке закрыт ({(int)e.StatusCode})",
        { } code => $"Сервер ответил {(int)code}",
        null => "Не удалось скачать: " + e.Message,
    };
}
