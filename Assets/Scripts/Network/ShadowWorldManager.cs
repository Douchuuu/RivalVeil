using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

/// <summary>
/// ShadowWorldManager — управление Shadow ID игроков в ShadowMultiplayer.
///
/// ═══════════════════════════════════════════════════════════════════
/// ЧТО ДЕЛАЕТ ЭТОТ КЛАСС
/// ═══════════════════════════════════════════════════════════════════
///
///   ТОЛЬКО назначает Shadow ID каждому игроку.
///   Shadow ID нужен для системы Ghost:
///   чтобы различать "ты видишь Ghost игрока 0 или игрока 1".
///   Назначение ID: сервер → клиенты через RPC.
///
/// ═══════════════════════════════════════════════════════════════════
/// ЧЕГО НЕ ДЕЛАЕТ (и не должен делать)
/// ═══════════════════════════════════════════════════════════════════
///
///   НЕ загружает сцены. Загрузкой PlayerWorldScene занимается
///   WorldSceneLoader.cs. Попытка загрузить сцену здесь создаёт конфликт:
///   - В SinglePlayer: ждёт 2 игроков → корутина висит вечно
///   - В ShadowMultiplayer: двойная загрузка PlayerWorldScene
///
///   Если нужно отслеживать события сцен — подпишись на
///   NetworkManager.SceneManager.OnSceneEvent в том месте где это уместно,
///   но не загружай сцены из этого класса.
/// </summary>
public class ShadowWorldManager : NetworkBehaviour
{
    private Dictionary<ulong, int> playerToShadowID = new Dictionary<ulong, int>();
    private int nextShadowID = 0;
    private int myLocalShadowID = -1;

    public static ShadowWorldManager Instance { get; private set; }

    void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
            Destroy(gameObject);
    }

    public override void OnNetworkSpawn()
    {
        // Назначаем Shadow ID только в Competitive режиме
        if (IsServer && GameModeManager.IsCompetitiveMode())
            AssignShadowIDs();
    }

    /// <summary>
    /// Назначает уникальный Shadow ID каждому подключённому игроку.
    /// ID используется GhostSync для различения чей Ghost отображается.
    /// Вызывается только на сервере при старте ShadowMultiplayer.
    /// </summary>
    private void AssignShadowIDs()
    {
        nextShadowID = 0;
        playerToShadowID.Clear();

        var players = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
        foreach (var player in players)
        {
            int shadowID = nextShadowID++;
            playerToShadowID[player.OwnerClientId] = shadowID;

            if (player.IsOwner)
            {
                // Хост — назначаем напрямую
                myLocalShadowID = shadowID;
                Debug.Log($"[ShadowWorld] Host Shadow ID: {shadowID}");
            }
            else
            {
                // Клиент — через RPC
                var rpcParams = RpcTarget.Single(player.OwnerClientId, RpcTargetUse.Temp);
                AssignShadowIDClientRpc(shadowID, rpcParams);
            }
        }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void AssignShadowIDClientRpc(int shadowID, RpcParams rpcParams = default)
    {
        myLocalShadowID = shadowID;
        Debug.Log($"[ShadowWorld] My Shadow ID: {shadowID}");
    }

    // ─── PUBLIC API ───────────────────────────────────────────────────────────

    public int GetPlayerShadowID(ulong playerId)
        => playerToShadowID.TryGetValue(playerId, out int id) ? id : -1;

    public int GetMyShadowID() => myLocalShadowID;
    public int GetTotalShadowWorlds() => nextShadowID;
}
