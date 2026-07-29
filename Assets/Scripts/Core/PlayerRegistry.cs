using System;
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════
/// РЕЕСТР ИГРОКОВ — ПОЛНАЯ ИСПРАВЛЕННАЯ ВЕРСИЯ
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class PlayerRegistry
{
    private PlayerStats _localPlayer;
    private PlayerStats _opponentPlayer;

    public event Action<PlayerStats> OnLocalPlayerRegistered;
    public event Action<PlayerStats> OnOpponentRegistered;

    // ─────────────────────────────────────────────────────────────────────────
    // КОНСТРУКТОР
    // ─────────────────────────────────────────────────────────────────────────

    public PlayerRegistry()
    {
        Debug.Log("[PlayerRegistry] Created by VContainer as Singleton");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РЕГИСТРАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public void RegisterLocalPlayer(PlayerStats stats)
    {
        _localPlayer = stats;
        OnLocalPlayerRegistered?.Invoke(stats);
        Debug.Log($"[PlayerRegistry] ✅ Local player registered: {stats.name}");
    }

    public void RegisterOpponent(PlayerStats stats)
    {
        _opponentPlayer = stats;
        OnOpponentRegistered?.Invoke(stats);
        Debug.Log($"[PlayerRegistry] ✅ Opponent registered: {stats.name}");
    }

    public void Unregister(PlayerStats stats)
    {
        if (_localPlayer == stats) _localPlayer = null;
        if (_opponentPlayer == stats) _opponentPlayer = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОЛУЧЕНИЕ (ИСПРАВЛЕНО: Добавлен GetAllOpponents для GameOverScreenManager)
    // ─────────────────────────────────────────────────────────────────────────

    public PlayerStats GetLocalPlayer() => _localPlayer;
    public PlayerStats GetOpponent() => _opponentPlayer;

    /// <summary>
    /// Возвращает список всех оппонентов. 
    /// Используется в GameOverScreenManager для сравнения результатов.
    /// </summary>
    public List<PlayerStats> GetAllOpponents()
    {
        var list = new List<PlayerStats>();
        if (_opponentPlayer != null)
        {
            list.Add(_opponentPlayer);
        }
        return list;
    }

    public bool HasLocalPlayer() => _localPlayer != null;
    public bool HasOpponent() => _opponentPlayer != null;

    public bool TryGetOpponent(out PlayerStats opponent)
    {
        opponent = _opponentPlayer;
        return opponent != null;
    }
}