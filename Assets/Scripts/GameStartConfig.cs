using UnityEngine;

/// <summary>
/// GameStartConfig v1.1 — статическая конфигурация запуска игры.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНО (v1.1):
/// ══════════════════════════════════════════════════════════════════════
///   - Добавлено подробное логирование для диагностики.
///   - Улучшены проверки на null.
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public static class GameStartConfig
{
    /// <summary>
    /// Флаг одиночной игры.
    /// Устанавливается в MenuManager перед загрузкой BaseScene.
    /// </summary>
    public static bool IsSinglePlayer { get; set; } = false;

    /// <summary>
    /// Код комнаты для матчмейкинга.
    /// </summary>
    public static string RoomCode { get; set; } = "";

    /// <summary>
    /// Join код для Relay.
    /// </summary>
    public static string RelayJoinCode { get; set; } = "";

    /// <summary>
    /// Имя оппонента (для competitive режима).
    /// </summary>
    public static string OpponentName { get; set; } = "";

    /// <summary>
    /// Сбросить все настройки.
    /// </summary>
    public static void Reset()
    {
        IsSinglePlayer = false;
        RoomCode = "";
        RelayJoinCode = "";
        OpponentName = "";
        Debug.Log("[GameStartConfig] Настройки сброшены.");
    }

    /// <summary>
    /// Получить строковое представление текущей конфигурации.
    /// </summary>
    public static string GetConfigString()
    {
        return $"[GameStartConfig] IsSinglePlayer={IsSinglePlayer}, RoomCode='{RoomCode}', " +
               $"RelayJoinCode='{RelayJoinCode}', OpponentName='{OpponentName}'";
    }
}
