using System;
using System.Collections.Generic;
using UnityEngine;

public abstract class AIControllerBase : MonoBehaviour
{
    public GameObject target { get; private set; }
    public List<WeaponsAAMissile> incomingMissiles { get; private set; }

    public ShipBase ship;
    public Rigidbody2D rb { get; private set; }

    public event Action<ShipBase> OnDamaged;

    protected virtual void Awake()
    {
        target = null;
        incomingMissiles = new List<WeaponsAAMissile>();
        rb = GetComponent<Rigidbody2D>();
    }

    protected virtual void Start()
    {
        ship.OnShipDamage += HandleDamageEvent;
        ship.OnSeekerFired += HandleSeekerFired;
    }

    protected virtual void HandleDamageEvent(object sender, ShipBase attacker)
    {
        OnDamaged?.Invoke(attacker);
    }

    protected virtual void HandleSeekerFired(object sender, WeaponsBase seeker)
    {
        seeker.SetTarget(target);
    }

    public void AddIncomingMissile(WeaponsAAMissile missile)
    {
        if (!incomingMissiles.Contains(missile))
        {
            incomingMissiles.Add(missile);
        }
    }

    public void RemoveIncomingMissile(WeaponsAAMissile missile)
    {
        incomingMissiles.Remove(missile);
    }

    public bool HasCloseIncomingMissile(float range)
    {
        if (incomingMissiles.Count == 0) return false;

        float rangeSqr = range * range;
        Vector2 myPos = rb.position;

        for (int i = incomingMissiles.Count - 1; i >= 0; i--)
        {
            WeaponsAAMissile missile = incomingMissiles[i];
            if (missile == null || !missile.gameObject.activeInHierarchy)
            {
                incomingMissiles.RemoveAt(i);
                continue;
            }

            float distSqr = ((Vector2)missile.transform.position - myPos).sqrMagnitude;
            if (distSqr <= rangeSqr)
                return true;
        }

        return false;
    }

    private void OnDisable()
    {
        ship.OnShipDamage -= HandleDamageEvent;
        ship.OnSeekerFired -= HandleSeekerFired;
    }

    public void SetTarget(GameObject newTarget)
    {
        if (newTarget == null || newTarget == target) return;
        if (!newTarget.activeInHierarchy) return;

        target = newTarget;
    }
}
