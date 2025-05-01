using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components; // Para NetworkTransform
using System.Collections.Generic; // Para List

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkTransform))] // Server Auth para movimiento controlado
[RequireComponent(typeof(Collider))] // Collider físico para que los jugadores se paren
public class MovingPlatform : NetworkBehaviour
{
    [Header("Movimiento")]
    [SerializeField] private List<Transform> waypoints = new List<Transform>(); // Puntos a seguir
    [SerializeField] private float speed = 2.0f;
    [SerializeField] private float waitTimeAtPoint = 1.0f; // Tiempo de espera en cada waypoint
    [SerializeField] private bool loop = true; 

    // Estado gestionado por el servidor
    private int currentWaypointIndex = 0;
    private bool isMovingForward = true;
    private bool isWaiting = false;
    private float waitTimer = 0f;

    // NetworkTransform (Server Auth) se encargará de sincronizar la posición calculada por el servidor.

    public override void OnNetworkSpawn()
    {
        // Verificar configuración
        if (waypoints.Count < 2)
        {
            Debug.LogError($"MovingPlatform {gameObject.name}: Necesita al menos 2 waypoints.");
            enabled = false;
            return;
        }
        var networkTransform = GetComponent<NetworkTransform>();
        if (networkTransform != null && !networkTransform.IsServerAuthoritative())
        {
             Debug.LogWarning($"MovingPlatform {gameObject.name}: NetworkTransform DEBE ser Server Authoritative.");
        }

        // Asegurar que la plataforma empieza en el primer waypoint (solo el servidor lo mueve)
        if (IsServer)
        {
            transform.position = waypoints[0].position;
        }
    }

    void Update()
    {
        // Solo el servidor calcula y ejecuta el movimiento
        if (!IsServer) return;

        // Si está esperando en un punto
        if (isWaiting)
        {
            waitTimer -= Time.deltaTime;
            if (waitTimer <= 0)
            {
                isWaiting = false;
                // Calcular siguiente índice
                CalculateNextWaypointIndex();
            }
            // No hacer nada más mientras espera
            return;
        }

        // Si hay waypoints y no está esperando
        if (waypoints.Count >= 2)
        {
            // Obtener waypoint actual y siguiente
            Transform currentWaypoint = waypoints[currentWaypointIndex];
            int nextWaypointIndex = GetNextWaypointIndex();
            Transform nextWaypoint = waypoints[nextWaypointIndex];

            // Calcular dirección y distancia
            Vector3 direction = (nextWaypoint.position - transform.position).normalized;
            float distanceToNext = Vector3.Distance(transform.position, nextWaypoint.position);

            // Si estamos muy cerca, hemos llegado
            if (distanceToNext < 0.1f)
            {
                // Ajustar posición exactamente al waypoint
                transform.position = nextWaypoint.position;
                // Empezar a esperar
                isWaiting = true;
                waitTimer = waitTimeAtPoint;
                currentWaypointIndex = nextWaypointIndex; // Actualizar índice actual para la próxima vez
            }
            else
            {
                // Mover la plataforma
                transform.position += direction * speed * Time.deltaTime;
            }
        }
    }

    private int GetNextWaypointIndex()
    {
        if (isMovingForward)
        {
            return (currentWaypointIndex + 1) % waypoints.Count;
        }
        else // Moviéndose hacia atrás (si no es loop)
        {
             // Esta lógica asume que ping-pong si no es loop
            return (currentWaypointIndex - 1 + waypoints.Count) % waypoints.Count;
        }
    }

    private void CalculateNextWaypointIndex() {
         if (isMovingForward) {
             if (currentWaypointIndex >= waypoints.Count - 1) { // Llegó al final
                 if (loop) {
                     currentWaypointIndex = 0; // Vuelve al inicio
                 } else {
                     isMovingForward = false; // Empieza a ir hacia atrás
                     // El índice actual ya es el último, el siguiente será el penúltimo
                 }
             }
             // Si no, el índice actual ya se actualizó al llegar al punto.
         } else { // Moviéndose hacia atrás
             if (currentWaypointIndex <= 0) { // Llegó al inicio (yendo hacia atrás)
                  isMovingForward = true; // Empieza a ir hacia adelante
                  // El índice actual es 0, el siguiente será 1
             }
              // Si no, el índice actual ya se actualizó al llegar al punto.
         }
    }



}
