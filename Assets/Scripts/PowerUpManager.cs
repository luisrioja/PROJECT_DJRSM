using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(NetworkObject))]
public class PowerUpManager : NetworkBehaviour
{
    [Header("Configuración de Spawn")]
    [SerializeField] private GameObject powerUpPrefab;
    [SerializeField] private List<Transform> spawnPoints = new List<Transform>();
    [SerializeField] private float spawnInterval = 15.0f;
    [SerializeField] private float initialDelay = 5.0f;
    [SerializeField] private int maxConcurrentPowerUps = 5;

    private List<NetworkObject> activePowerUps = new List<NetworkObject>();

    public override void OnNetworkSpawn()
    {
        if (!IsServer) {
            enabled = false;
            return;
        }
        if (powerUpPrefab == null || spawnPoints.Count == 0) {
            Debug.LogError("PowerUpManager: Falta prefab o spawn points.");
            enabled = false;
            return;
        }
        StartCoroutine(SpawnPowerUpsCoroutine());
    }

    private IEnumerator SpawnPowerUpsCoroutine()
    {
        yield return new WaitForSeconds(initialDelay);
        while (true)
        {
            activePowerUps.RemoveAll(item => item == null || !item.IsSpawned);
            if (activePowerUps.Count < maxConcurrentPowerUps)
            {
                SpawnSinglePowerUp();
            }
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    private void SpawnSinglePowerUp()
    {
        if (!IsServer) return;

        int spawnIndex = Random.Range(0, spawnPoints.Count);
        Transform spawnPoint = spawnPoints[spawnIndex];

        // --- CORRECCIÓN AQUÍ: Quitar Player. ---
        PowerUpType randomType = (PowerUpType)Random.Range(0, System.Enum.GetValues(typeof(PowerUpType)).Length);
        // --- FIN CORRECCIÓN ---

        GameObject powerUpInstance = Instantiate(powerUpPrefab, spawnPoint.position, spawnPoint.rotation);
        NetworkObject powerUpNetworkObject = powerUpInstance.GetComponent<NetworkObject>();
        PowerUp powerUpScript = powerUpInstance.GetComponent<PowerUp>();

        if (powerUpNetworkObject != null && powerUpScript != null)
        {
            // --- CORRECCIÓN AQUÍ: Quitar Player. ---
            powerUpScript.Initialize(randomType); // Initialize tipo
            // --- FIN CORRECCIÓN ---
            powerUpNetworkObject.Spawn(true);     // Spawn en red
            activePowerUps.Add(powerUpNetworkObject); // Add a lista
        }
        else
        {
            Debug.LogError("PowerUpManager: Prefab de PowerUp mal configurado.");
            Destroy(powerUpInstance);
        }
    }

     public override void OnNetworkDespawn() {
         if (IsServer) {
             StopAllCoroutines();
             foreach(var powerupNetObj in activePowerUps) {
                 if (powerupNetObj != null && powerupNetObj.IsSpawned) {
                     powerupNetObj.Despawn(true);
                 }
             }
             activePowerUps.Clear();
         }
         base.OnNetworkDespawn();
     }
}
