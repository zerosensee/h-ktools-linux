global using HKTools.Core;
global using HKTools.Media;
global using static HKTools.Core.FieldType;

namespace HKTools.Scripts;

public static class Common
{
    public static readonly (string, string) ReplaceWarning = ("Замена исходного файла",
        "Исходный файл заменяется готовым результатом только после успешной проверки (ffprobe). До подтверждения результата исходный файл сохраняется.");

    public static List<Field> OutputFields(string group = "Вывод") =>
    [
        new Field("overwrite_source", "Заменить исходный файл результатом", Check, false, group)
            .Hint("Только после успешной проверки результата").Warn(ReplaceWarning.Item1, ReplaceWarning.Item2),
        new Field("delete_source", "Удалить оригинал после обработки (в корзину)", Check, false, group).When(new Cond("overwrite_source", false)),
    ];

    public static Field DeleteOriginal(string group = "Общие", string key = "delete_original") =>
        new(key, "Удалить исходный файл (в корзину)", Check, false, group);

    public static string Fn(string p) => Path.GetFileName(p);
    public static bool NonEmpty(string p) => File.Exists(p) && new FileInfo(p).Length > 0;
    public static string F(double v, string fmt) => v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
}
