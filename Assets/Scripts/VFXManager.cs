using UnityEngine;
using Unity.Netcode;

public class VFXManager : NetworkBehaviour
{
    public static VFXManager Instance { get; private set; }

    [Header("Efectos Visuales Prefabs")]
    [Tooltip("Prefab del efecto visual para el impacto de bala.")]
    [SerializeField] private GameObject bulletImpactEffectPrefab;
    [Tooltip("Prefab del efecto visual para la muerte del jugador.")]
    [SerializeField] private GameObject playerDeathEffectPrefab;

    [Header("Duraciones de Efectos (en segundos)")]
    [Tooltip("Cuánto tiempo permanece el efecto de impacto de bala antes de destruirse.")]
    [SerializeField] private float bulletImpactDuration = 2f;
    [Tooltip("Cuánto tiempo permanece el efecto de muerte del jugador antes de destruirse.")]
    [SerializeField] private float playerDeathDuration = 3f;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Ya existe una instancia de VFXManager. Destruyendo esta nueva.", gameObject);
            Destroy(gameObject);
        }
        else
        {
            Instance = this;
        }
    }

    // --- Client RPCs para Efectos ---

    /// <summary>
    /// Muestra el efecto de impacto de bala en todos los clientes.
    /// Llamado por el servidor.
    /// </summary>
    [ClientRpc]
    public void PlayBulletImpactClientRpc(Vector3 position, Vector3 normal)
    {
        // Este ClientRpc se ejecutará en todos los clientes.
        // El host (servidor+cliente) también lo ejecutará.
        // Un servidor dedicado (sin componente visual) no ejecutará esto porque no es un cliente.
        if (bulletImpactEffectPrefab != null)
        {
            GameObject effect = Instantiate(bulletImpactEffectPrefab, position, Quaternion.LookRotation(normal));
            Destroy(effect, bulletImpactDuration);
        }
        else
        {
            Debug.LogWarning("VFXManager: bulletImpactEffectPrefab no está asignado.", this);
        }
    }

    /// <summary>
    /// Muestra el efecto de muerte del jugador en todos los clientes.
    /// Llamado por el servidor.
    /// </summary>
    [ClientRpc]
    public void PlayPlayerDeathClientRpc(Vector3 position, Quaternion rotation)
    {
        if (playerDeathEffectPrefab != null)
        {
            GameObject effect = Instantiate(playerDeathEffectPrefab, position, rotation);
            Destroy(effect, playerDeathDuration);
        }
        else
        {
            Debug.LogWarning("VFXManager: playerDeathEffectPrefab no está asignado.", this);
        }
    }
}
