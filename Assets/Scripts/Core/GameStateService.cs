using System;
using System.Collections.Generic;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════
/// GAME STATE SERVICE — единый источник правды о состоянии паузы
/// ══════════════════════════════════════════════════════════════════════
///
/// БЫЛО (проблема):
///   IsPaused — простой флаг. При вложенных паузах одна Resume() снимала всё:
///
///   Сценарий-баг:
///     Pause(LevelUp)   → IsPaused = true,  reason = LevelUp
///     Pause(GameOver)  → IsPaused = true,  reason = GameOver (перезаписал!)
///     Resume()         ← LevelUpManager.CloseMenu() вызывает при закрытии
///     → IsPaused = false ← ИГРА ВОЗОБНОВЛЯЕТСЯ несмотря на GameOver!
///
/// СТАЛО (решение — Set-Based паузы):
///   _activePauses: HashSet — хранит все активные причины паузы.
///   Pause(reason)    → добавляет причину в Set.
///   Resume(reason)   → убирает конкретную причину из Set.
///   IsPaused         → true пока Set не пуст.
///   CurrentPauseReason → причина с наибольшим приоритетом из Set.
///
/// ПРИОРИТЕТЫ (для CurrentPauseReason — инфо для отладки):
///   GameOver > LevelUp
///
/// ОБРАТНАЯ СОВМЕСТИМОСТЬ:
///   Resume() без аргумента (старый код) → снимает последнюю добавленную причину.
///   Resume(reason)                       → снимает конкретную причину.
///
/// РЕГИСТРАЦИЯ в GameLifetimeScope:
///   builder.Register<GameStateService>(Lifetime.Singleton);
/// </summary>
public enum PauseReason
{
    None     = 0,
    LevelUp  = 1,   // пауза при выборе карточки прокачки
    GameOver = 2,   // игра окончена — наивысший приоритет
    // Резерв для будущих механик:
    // Shop    = 3,
    // Cutscene = 4,
    // Loading  = 5,
}

public class GameStateService
{
    // ─── СОСТОЯНИЕ ───────────────────────────────────────────────────────────

    // ИСПРАВЛЕНИЕ: Set вместо одного флага — поддержка вложенных пауз.
    private readonly HashSet<PauseReason> _activePauses = new HashSet<PauseReason>();
    // Стек порядка добавления — для Resume() без аргумента (обратная совместимость).
    private readonly Stack<PauseReason>   _pauseOrder   = new Stack<PauseReason>();

    /// <summary>
    /// true если игра на паузе по любой причине.
    /// Аналог старого LevelUpManager.IsPaused.
    /// </summary>
    public bool IsPaused => _activePauses.Count > 0;

    /// <summary>
    /// Причина текущей паузы с наибольшим приоритетом.
    /// None если пауза не активна.
    /// </summary>
    public PauseReason CurrentPauseReason
    {
        get
        {
            if (_activePauses.Count == 0)   return PauseReason.None;
            if (_activePauses.Contains(PauseReason.GameOver)) return PauseReason.GameOver;
            if (_activePauses.Contains(PauseReason.LevelUp))  return PauseReason.LevelUp;
            return PauseReason.None;
        }
    }

    // ─── СОБЫТИЯ ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Вызывается когда игра ставится на паузу по новой причине.
    /// </summary>
    public event Action<PauseReason> OnPaused;

    /// <summary>
    /// Вызывается когда все паузы сняты (IsPaused переходит в false).
    /// </summary>
    public event Action OnResumed;

    // ─── API ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Добавляет причину паузы.
    /// Если эта причина уже активна — повторный вызов игнорируется.
    /// Несколько разных причин могут быть активны одновременно.
    /// </summary>
    public void Pause(PauseReason reason)
    {
        if (reason == PauseReason.None) return;

        bool wasAlreadyPaused = IsPaused;
        _activePauses.Add(reason);
        _pauseOrder.Push(reason);

        OnPaused?.Invoke(reason);
        UnityEngine.Debug.Log($"[GameStateService] PAUSE + {reason} | Active: {GetActiveReasonsString()}");
    }

    /// <summary>
    /// Снимает конкретную причину паузы.
    /// Если остаются другие причины — игра остаётся на паузе.
    /// Событие OnResumed вызывается только когда убраны ВСЕ причины.
    /// </summary>
    public void Resume(PauseReason reason)
    {
        if (reason == PauseReason.None) return;

        bool removed = _activePauses.Remove(reason);
        if (!removed) return; // этой причины и не было

        UnityEngine.Debug.Log($"[GameStateService] RESUME - {reason} | Remaining: {GetActiveReasonsString()}");

        if (_activePauses.Count == 0)
        {
            _pauseOrder.Clear();
            OnResumed?.Invoke();
            UnityEngine.Debug.Log("[GameStateService] ✅ Все паузы сняты — игра возобновлена.");
        }
    }

    /// <summary>
    /// Снимает последнюю добавленную причину паузы.
    /// Для обратной совместимости со старым кодом который вызывал Resume() без аргумента.
    /// </summary>
    public void Resume()
    {
        if (_pauseOrder.Count == 0) return;

        // Ищем последнюю причину которая ещё активна
        PauseReason toRemove = PauseReason.None;
        while (_pauseOrder.Count > 0)
        {
            var candidate = _pauseOrder.Pop();
            if (_activePauses.Contains(candidate))
            {
                toRemove = candidate;
                break;
            }
        }

        if (toRemove != PauseReason.None)
            Resume(toRemove);
    }

    /// <summary>
    /// Снимает ВСЕ активные паузы принудительно.
    /// Использовать только при экстренном сбросе (смена сцены, рестарт).
    /// </summary>
    public void ForceResumeAll()
    {
        if (_activePauses.Count == 0) return;

        UnityEngine.Debug.Log($"[GameStateService] FORCE RESUME ALL | Was: {GetActiveReasonsString()}");
        _activePauses.Clear();
        _pauseOrder.Clear();
        OnResumed?.Invoke();
    }

    /// <summary>
    /// Проверяет активна ли пауза по конкретной причине.
    /// </summary>
    public bool IsPausedBy(PauseReason reason) => _activePauses.Contains(reason);

    // ─── ВСПОМОГАТЕЛЬНЫЕ ─────────────────────────────────────────────────────

    private string GetActiveReasonsString()
    {
        if (_activePauses.Count == 0) return "none";
        return string.Join(", ", _activePauses);
    }
}
