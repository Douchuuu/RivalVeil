using Unity.Netcode;
using UnityEngine;

public class NetworkUI : MonoBehaviour
{
    // ✅ ФИКС: OnGUI вызывается НЕСКОЛЬКО РАЗ за кадр (Layout + Repaint events).
    // Без защиты одно нажатие кнопки HOST/CLIENT триггерит StartHost()/StartClient() дважды.
    // Unity Transport получает двойной запрос на коннект — второй вызывает ошибку/сброс.
    // Решение: флаг на один кадр блокирует повторное срабатывание.
    private bool networkActionTakenThisFrame = false;
    private float modeButtonCooldown = 0f;
    private const float MODE_BUTTON_DELAY = 1.5f;

    void Update()
    {
        // Сбрасываем флаг каждый кадр (не в OnGUI, т.к. он вызывается несколько раз за кадр)
        networkActionTakenThisFrame = false;
        modeButtonCooldown -= Time.unscaledDeltaTime;
    }

    void OnGUI()
    {
        if (NetworkManager.Singleton == null) return;

        // ✅ НОВОЕ: Скрыть меню когда режим выбран!
        if (!GameModeManager.IsSelecting())
        {
            return; // Меню исчезает после выбора режима
        }
        // Проверка: Если менеджера нет на сцене, просто выходим
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        // Красивый стиль
        GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        GUI.contentColor = Color.white;

        GUILayout.BeginArea(new Rect(10, 10, 350, 500));

        // ════════════════════════════════════════════════════════════════════════════
        // ЗАГОЛОВОК
        // ════════════════════════════════════════════════════════════════════════════
        GUILayout.Label("🌐 NETWORK & GAME MODES", GUI.skin.box);

        // ════════════════════════════════════════════════════════════════════════════
        // СЕТЕВЫЕ КНОПКИ (Host / Client)
        // ════════════════════════════════════════════════════════════════════════════
        GUILayout.Label("📡 Network Connection:", GUI.skin.label);

        if (!NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
        {
            // Не подключены - показываем кнопки подключения
            GUI.backgroundColor = new Color(0.3f, 0.3f, 0.3f, 0.9f);
            GUILayout.Label("Not connected. Choose:", GUI.skin.box);
            GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);

            GUI.backgroundColor = new Color(0, 0.5f, 0, 0.9f);
            if (GUILayout.Button("🔴 HOST (Create Server)", GUILayout.Height(40)))
            {
                // ✅ ФИКС: Проверяем флаг — OnGUI вызывается несколько раз за кадр
                if (!networkActionTakenThisFrame)
                {
                    networkActionTakenThisFrame = true;
                    NetworkManager.Singleton.StartHost();
                    Debug.Log("🔴 Starting as HOST...");
                }
            }
            GUI.backgroundColor = new Color(0, 0, 0.5f, 0.9f);
            if (GUILayout.Button("🔵 CLIENT (Connect)", GUILayout.Height(40)))
            {
                // ✅ ФИКС: Проверяем флаг — OnGUI вызывается несколько раз за кадр
                if (!networkActionTakenThisFrame)
                {
                    networkActionTakenThisFrame = true;
                    NetworkManager.Singleton.StartClient();
                    Debug.Log("🔵 Starting as CLIENT...");
                }
            }
            GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        }
        else
        {
            // Подключены - показываем статус
            string mode = NetworkManager.Singleton.IsHost ? "🔴 HOST" : "🔵 CLIENT";
            GUI.backgroundColor = new Color(0, 0.5f, 0, 0.9f);
            GUILayout.Label($"✅ Connected as: {mode}", GUI.skin.box);
            GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        }

        GUILayout.Space(15);

        // ════════════════════════════════════════════════════════════════════════════
        // КНОПКИ ПЕРЕКЛЮЧЕНИЯ ИГРОВЫХ РЕЖИМОВ
        // ════════════════════════════════════════════════════════════════════════════
        GUILayout.Label("⚙️ Game Modes:", GUI.skin.label);

        // Проверяем что NetworkManager запущен
        if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
        {
            // Режимы доступны
            if (GameModeManager.Instance != null)
            {
                // ✅ НОВОЕ: Проверяем что GameModeManager spawned
                if (!GameModeManager.Instance.IsSpawned)
                {
                    GUI.backgroundColor = new Color(0.5f, 0.4f, 0, 0.9f);
                    GUILayout.Label("⏳ Initializing...\n\nWaiting for GameModeManager", GUI.skin.box);
                    GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
                }
                else
                {
                    // ✅ ФИКС: Кнопки режима заблокированы на 1.5 сек после нажатия
                    // + защита от двойного срабатывания OnGUI
                    bool modeReady = modeButtonCooldown <= 0f && !networkActionTakenThisFrame;
                    GUI.enabled = modeReady;

                    // Single Player
                    GUI.backgroundColor = new Color(0.3f, 0.2f, 0.5f, 0.9f);
                    if (GUILayout.Button("🎮 Single Player\n(10 min timer, pauses on level up)", GUILayout.Height(50)))
                    {
                        networkActionTakenThisFrame = true;
                        modeButtonCooldown = MODE_BUTTON_DELAY;
                        GameModeManager.Instance.SetGameModeServerRpc((int)GameMode.SinglePlayer);
                        Debug.Log("✅ Mode: Single Player");
                    }
                    // Shadow Multiplayer
                    GUI.backgroundColor = new Color(0.5f, 0.2f, 0.5f, 0.9f);
                    if (GUILayout.Button("🌓 Shadow Multiplayer\n(Competitive - 20 min timer, separate worlds)", GUILayout.Height(50)))
                    {
                        networkActionTakenThisFrame = true;
                        modeButtonCooldown = MODE_BUTTON_DELAY;
                        GameModeManager.Instance.SetGameModeServerRpc((int)GameMode.ShadowMultiplayer);
                        Debug.Log("✅ Mode: Shadow Multiplayer");
                    }
                    GUI.enabled = true;

                    if (modeButtonCooldown > 0f)
                    {
                        GUI.backgroundColor = new Color(0.5f, 0.4f, 0, 0.9f);
                        GUILayout.Label($"⏳ Applying... ({modeButtonCooldown:F1}s)", GUI.skin.box);
                    }

                    // Информация о текущем режиме
                    GUILayout.Space(10);
                    GUI.backgroundColor = new Color(0.2f, 0.4f, 0.2f, 0.9f);
                    GameMode currentMode = GameModeManager.GetCurrentMode();
                    GUILayout.Label($"📊 Current Mode: {currentMode}", GUI.skin.box);
                }
            }
            else
            {
                // GameModeManager не найден
                GUI.backgroundColor = new Color(0.5f, 0.2f, 0.2f, 0.9f);
                GUILayout.Label("⚠️ GameModeManager NOT FOUND!", GUI.skin.box);
                GUILayout.Label("Create GameObject 'GameModeManager'", GUI.skin.label);
                GUILayout.Label("Add Component → GameModeManager", GUI.skin.label);
            }
        }
        else
        {
            // Режимы недоступны - ждем подключения
            GUI.backgroundColor = new Color(0.5f, 0.4f, 0, 0.9f);
            GUILayout.Label("⏳ Waiting...\n\nPress HOST or CLIENT first!", GUI.skin.box);
        }

        GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        GUILayout.EndArea();
    }
}