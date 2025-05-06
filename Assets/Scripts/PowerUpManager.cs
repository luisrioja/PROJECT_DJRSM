using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic; // Necesario para List<>

public class PowerUpManager : NetworkBehaviour
{
    [Header("Configuración de Spawn")]
    [Tooltip("Lista de posibles prefabs de PowerUp a instanciar. Deben tener PowerUp.cs y NetworkObject.")]
    [SerializeField] private List<GameObject> powerUpPrefabs = new List<GameObject>();

    [Tooltip("Lista de Transforms que marcan las posiciones donde pueden aparecer los PowerUps.")]
    [SerializeField] private List<Transform> spawnPoints = new List<Transform>();

    [Tooltip("Número máximo de PowerUps activos simultáneamente en el mapa.")]
    [SerializeField] private int maxPowerUps = 5;

    [Tooltip("Tiempo en segundos entre intentos de spawn de un nuevo PowerUp si hay espacio.")]
    [SerializeField] private float spawnInterval = 10.0f;

    [Tooltip("Distancia mínima requerida entre un punto de spawn y cualquier PowerUp existente para considerarlo 'libre'.")]
    [SerializeField] private float minSpawnDistance = 2.0f; // Evita spawns superpuestos

    // Lista interna para rastrear los PowerUps activos (solo en el servidor)
    private List<NetworkObject> activePowerUps = new List<NetworkObject>();

    // =========================================
    // --- Ciclo de Vida y Red ---
    // =========================================

    public override void OnNetworkSpawn()
    {
        // Solo el servidor se encarga de gestionar el spawn de PowerUps.
        if (!IsServer) return;

        // --- Validaciones Iniciales ---
        if (spawnPoints == null || spawnPoints.Count == 0)
        {
            Debug.LogError("PowerUpManager: ¡No hay puntos de spawn ('Spawn Points') asignados en el Inspector!");
            enabled = false; // Desactivar script si no puede funcionar.
            return;
        }

        if (powerUpPrefabs == null || powerUpPrefabs.Count == 0)
        {
            Debug.LogError("PowerUpManager: ¡La lista de prefabs de PowerUp ('Power Up Prefabs') está vacía o no asignada!");
            enabled = false;
            return;
        }

        // Validar cada prefab en la lista
        bool validationFailed = false;
        for (int i = 0; i < powerUpPrefabs.Count; i++)
        {
            GameObject prefab = powerUpPrefabs[i];
            if (prefab == null)
            {
                Debug.LogError($"PowerUpManager: ¡Hay una entrada NULA en la lista 'Power Up Prefabs' en el índice {i}!");
                validationFailed = true;
                continue; // Continuar revisando los demás
            }
            if (prefab.GetComponent<NetworkObject>() == null)
            {
                 Debug.LogError($"PowerUpManager: El prefab '{prefab.name}' (índice {i}) en la lista NO tiene el componente NetworkObject requerido.");
                 validationFailed = true;
            }
            if (prefab.GetComponent<PowerUp>() == null)
             {
                 Debug.LogError($"PowerUpManager: El prefab '{prefab.name}' (índice {i}) en la lista NO tiene el componente PowerUp requerido.");
                 validationFailed = true;
            }
        }
        if (validationFailed) {
            enabled = false; // Desactivar si alguna validación falló
            return;
        }
        // --- Fin Validaciones ---


        activePowerUps = new List<NetworkObject>(); // Inicializar la lista de seguimiento

        // Iniciar el bucle de spawn si el intervalo es válido.
        if (spawnInterval > 0)
        {
            StartCoroutine(SpawnLoop());
            Debug.Log("PowerUpManager (Servidor): Iniciando bucle de spawn de PowerUps.");
        }
        else
        {
            Debug.LogWarning("PowerUpManager: Intervalo de spawn es <= 0. No habrá spawn automático.");
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            StopAllCoroutines();
        }
        base.OnNetworkDespawn();
    }


    // =========================================
    // --- Lógica de Spawn (Solo Servidor) ---
    // =========================================

    private IEnumerator SpawnLoop()
    {
        while (enabled && IsServer)
        {
            yield return new WaitForSeconds(spawnInterval);
            CleanUpDespawnedPowerUps();
            if (activePowerUps.Count < maxPowerUps)
            {
                TrySpawnSinglePowerUp();
            }
        }
    }

    private void TrySpawnSinglePowerUp()
    {
        if (!IsServer) return;

        Transform spawnPoint = GetAvailableSpawnPoint();
        if (spawnPoint == null)
        {
            return;
        }

        int randomIndex = Random.Range(0, powerUpPrefabs.Count);
        GameObject prefabToSpawn = powerUpPrefabs[randomIndex];

        if (prefabToSpawn == null) {
            Debug.LogError($"PowerUpManager: ¡El prefab aleatorio seleccionado (índice {randomIndex}) es NULL! Saltando spawn.");
            return;
        }

        GameObject powerUpInstance = Instantiate(prefabToSpawn, spawnPoint.position, spawnPoint.rotation);
        NetworkObject netObj = powerUpInstance.GetComponent<NetworkObject>();

        if (netObj != null)
        {
            // Spawnear el objeto en la red
            netObj.Spawn(true);
            activePowerUps.Add(netObj);

            // Obtener el tipo del script PowerUp usando la variable pública 'Type'
            PowerUp puScript = powerUpInstance.GetComponent<PowerUp>();
            string puTypeString = "TipoDesconocido"; // Valor por defecto
            if (puScript != null)
            {
                 puTypeString = puScript.Type.ToString(); // <--- USA LA VARIABLE 'Type'
            }
            else {
                 Debug.LogWarning($"PowerUpManager: El prefab instanciado '{prefabToSpawn.name}' no tiene el script PowerUp para obtener el tipo.");
            }

            Debug.Log($"PowerUpManager: Spawneado '{prefabToSpawn.name}' (Tipo: {puTypeString}) en {spawnPoint.name}. Total activos: {activePowerUps.Count}/{maxPowerUps}");
        }
        else
        {
            Debug.LogError($"PowerUpManager: ¡El prefab instanciado '{prefabToSpawn.name}' no tiene NetworkObject! Destruyendo instancia local.");
            Destroy(powerUpInstance);
        }
    }

    private void TrySpawnMultiple(int count)
    {
        if (!IsServer) return;
        for (int i = 0; i < count && activePowerUps.Count < maxPowerUps; i++)
        {
            TrySpawnSinglePowerUp();
        }
    }

    // =========================================
    // --- Métodos Auxiliares (Solo Servidor) ---
    // =========================================

    private Transform GetAvailableSpawnPoint()
    {
        List<Transform> candidates = new List<Transform>(spawnPoints);
        ShuffleList(candidates);

        foreach (Transform point in candidates)
        {
            if (point == null) continue;

            bool pointIsFree = true;
            foreach (NetworkObject activePowerUp in activePowerUps)
            {
                if (activePowerUp == null || !activePowerUp.IsSpawned) continue;

                if (Vector3.Distance(point.position, activePowerUp.transform.position) < minSpawnDistance)
                {
                    pointIsFree = false;
                    break;
                }
            }

            if (pointIsFree)
            {
                return point;
            }
        }
        return null;
    }

    private void CleanUpDespawnedPowerUps()
    {
        int removedCount = activePowerUps.RemoveAll(item => item == null || !item.IsSpawned);

    }

    private void ShuffleList<T>(List<T> list)
    {
        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = Random.Range(0, n + 1);
            T value = list[k];
            list[k] = list[n];
            list[n] = value;
        }
    }
}
