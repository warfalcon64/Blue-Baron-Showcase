using System.Collections;
using UnityEngine;

public class FlareLauncher : CombatSystemBase
{
    [Header("Flare Settings")]
    [SerializeField] private Flare flarePrefab;
    [SerializeField] private int flareCount = 4;
    [SerializeField] private float spreadAngle = 45f;
    [SerializeField] private float burstInterval = 0.1f;
    private bool isFiring;

    public override void Activate()
    {
        if (!IsReady() || isFiring) return;

        if (maxCharges > 0)
            currentCharges--;

        nextReadyTime = Time.time + cooldown;
        StartCoroutine(FireBurst());
    }

    private IEnumerator FireBurst()
    {
        isFiring = true;

        for (int i = 0; i < flareCount; i++)
        {
            // Alternate sides for flare launch
            float angle = (i % 2 == 0) ? spreadAngle : -spreadAngle;
            Vector2 backward = -ship.transform.up;
            Vector2 direction = Quaternion.Euler(0, 0, angle) * backward;

            Vector2 spawnPos = (Vector2)transform.position;
            Flare flare = ObjectPoolManager.SpawnObject(flarePrefab, spawnPos, Quaternion.identity, ObjectPoolManager.PoolType.Flare);
            flare.Setup(direction, ship.GetRigidBody().linearVelocity, ship);

            if (i < flareCount - 1)
                yield return new WaitForSeconds(burstInterval);
        }

        isFiring = false;
    }
}
