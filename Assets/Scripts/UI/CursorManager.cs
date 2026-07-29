using UnityEngine;

public class CursorManager : MonoBehaviour
{
    // ✅ ФИКС: Кэшируем LevelUpManager один раз
    private LevelUpManager levelUpManager;

    void Start()
    {
        // ✅ Ищем один раз при старте, не каждый кадр
        levelUpManager = FindFirstObjectByType<LevelUpManager>();
    }

    void Update()
    {
        // GameOver — курсор нужен для кнопки Return
        if (GameOverScreenManager.IsGameOver)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            return;
        }

        if (GameModeManager.IsSelecting())
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            return;
        }

        // ✅ ФИКС: Используем закэшированный levelUpManager
        if (levelUpManager != null && levelUpManager.IsShowing)
        {
            return;
        }

        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
    }
}