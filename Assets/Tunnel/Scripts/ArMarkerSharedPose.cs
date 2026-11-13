using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One headset's sighting of the AR marker, or the shared pose the host publishes: the cube's pose in the shared
/// tabletop frame, when the camera images were taken (on the shared network clock), how trustworthy it is, and which
/// cameras showed the marker.
/// </summary>
[Serializable]
public struct ArMarkerSample : INetworkSerializable, IEquatable<ArMarkerSample>
{
    public const byte LeftCamera = 1, RightCamera = 2;

    public Vector3 position;
    public Quaternion rotation;
    public double time;
    public float quality;
    public byte cameras;
    public ulong source;
    public bool valid;
    public ushort handoffs;

    public Pose pose => new(position, rotation);
    public bool both => (cameras & (LeftCamera | RightCamera)) == (LeftCamera | RightCamera);

    public string CamerasText() => (cameras & 3) switch { 3 => "L+R", 1 => "L only", 2 => "R only", _ => "none" };

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref position);
        serializer.SerializeValue(ref rotation);
        serializer.SerializeValue(ref time);
        serializer.SerializeValue(ref quality);
        serializer.SerializeValue(ref cameras);
        serializer.SerializeValue(ref source);
        serializer.SerializeValue(ref valid);
        serializer.SerializeValue(ref handoffs);
    }

    public bool Equals(ArMarkerSample other) =>
        position == other.position && rotation == other.rotation && time == other.time && quality == other.quality &&
        cameras == other.cameras && source == other.source && valid == other.valid && handoffs == other.handoffs;

    public override bool Equals(object obj) => obj is ArMarkerSample other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(position, rotation, time, source, valid);
}

/// <summary>
/// Sighting times are on a clock that changes when this headset joins or leaves a session (the app's own clock
/// alone, the shared network clock in a session). A sighting stamped well ahead of now, or well behind the one
/// before it, was stamped on the other clock: it is dropped rather than kept fresh for ever.
/// </summary>
public static class ArMarkerClock
{
    /// <summary>A sighting stamped more than this (seconds) after now is from another clock.</summary>
    public const double futureTolerance = 0.5;

    /// <summary>A sighting stamped more than this (seconds) before the latest one means the clock changed.</summary>
    public const double jump = 1.0;

    /// <summary>How old a sighting is at <paramref name="now"/>, or +infinity when it is from another clock.</summary>
    public static double Age(double now, double time)
    {
        double age = now - time;
        return age < -futureTolerance ? double.PositiveInfinity : Math.Max(age, 0.0);
    }

    /// <summary>
    /// The shared clock in a session: the host's own, and on a joining headset the best estimate of the host's
    /// clock right now. (Netcode's ServerTime on a joining headset runs half a round trip plus a 50 ms buffer behind
    /// the host's, so its sightings looked 100 ms or more older to the host than they were; LocalTime runs a tick
    /// ahead of the host's.)
    /// </summary>
    public static double SharedNow(Unity.Netcode.NetworkManager manager)
    {
        if (manager == null)
            return Time.realtimeSinceStartupAsDouble;
        if (manager.IsServer || manager.NetworkTimeSystem == null)
            return manager.ServerTime.Time;
        return manager.LocalTime.Time - manager.NetworkTimeSystem.LocalBufferSec;
    }
}

/// <summary>
/// The host's choice of which headset's sightings become the shared pose: the best fresh one, but a new source takes
/// over only after it has been clearly better for a while (or the current one goes stale), so the source doesn't
/// flicker between headsets that see the marker about equally well.
/// </summary>
public class ArMarkerArbiter
{
    [Tooltip("A sighting older than this (seconds) no longer counts.")]
    public float staleAfter = 0.35f;
    [Tooltip("How much better (quality, 0-1) another headset must be to take over.")]
    public float margin = 0.12f;
    [Tooltip("For how long (seconds) it must stay that much better.")]
    public float sustain = 0.6f;
    [Tooltip("When another headset's latest sighting is this much newer (seconds) than the source's, the source has gone quiet and is replaced.")]
    public float silentFor = 0.2f;
    [Tooltip("After a change of source, the new one is kept at least this long (seconds) unless it goes stale.")]
    public float minHold = 0.5f;

    readonly Dictionary<ulong, ArMarkerSample> m_Latest = new();
    readonly Dictionary<ulong, float> m_Quality = new(); // smoothed
    bool m_HasSource;
    ulong m_Source;
    bool m_HasChallenger;
    ulong m_Challenger;
    double m_ChallengerSince;
    double m_SwitchedAt = double.NegativeInfinity;
    ArMarkerSample m_Published;

    public int handoffs { get; private set; }
    public bool hasSource => m_HasSource;
    public ulong source => m_Source;
    public ArMarkerSample published => m_Published;

    /// <summary>Sightings dropped because they were stamped on another clock (see <see cref="ArMarkerClock"/>).</summary>
    public int fromAnotherClock { get; private set; }

    /// <summary>Every headset's latest sighting.</summary>
    public IReadOnlyDictionary<ulong, ArMarkerSample> latest => m_Latest;

    /// <summary>Forget every sighting and the source (the clock changed, or the search restarts).</summary>
    public void Reset()
    {
        m_Latest.Clear();
        m_Quality.Clear();
        m_HasSource = false;
        m_HasChallenger = false;
        m_SwitchedAt = double.NegativeInfinity;
        m_Published = default;
        handoffs = 0;
    }

    /// <summary>A sighting from a headset (its <see cref="ArMarkerSample.source"/> says which).</summary>
    public void Submit(ArMarkerSample sample)
    {
        if (m_Latest.TryGetValue(sample.source, out var previous) && previous.time >= sample.time)
        {
            if (previous.time - sample.time < ArMarkerClock.jump)
                return; // out of order
            fromAnotherClock++; // the one kept was stamped on another clock
            m_Quality.Remove(sample.source);
        }
        m_Latest[sample.source] = sample;
        m_Quality[sample.source] = m_Quality.TryGetValue(sample.source, out float q) ? Mathf.Lerp(q, sample.quality, 0.3f) : sample.quality;
    }

    /// <summary>A player left.</summary>
    public void Forget(ulong client)
    {
        m_Latest.Remove(client);
        m_Quality.Remove(client);
    }

    public float SmoothedQuality(ulong client) => m_Quality.TryGetValue(client, out float q) ? q : 0f;

    bool Fresh(double now, ulong client, out ArMarkerSample sample) =>
        m_Latest.TryGetValue(client, out sample) && ArMarkerClock.Age(now, sample.time) <= staleAfter;

    // Sightings stamped on another clock never become fresh: drop them.
    void DropFromAnotherClock(double now)
    {
        List<ulong> drop = null;
        foreach (var pair in m_Latest)
            if (now - pair.Value.time < -ArMarkerClock.futureTolerance)
                (drop ??= new List<ulong>()).Add(pair.Key);
        if (drop == null)
            return;
        foreach (var client in drop)
        {
            m_Latest.Remove(client);
            m_Quality.Remove(client);
            fromAnotherClock++;
        }
    }

    /// <summary>
    /// Picks the source at time <paramref name="now"/> and returns the pose to publish (not valid when every
    /// sighting is stale). <paramref name="change"/> says why the source changed, or is null.
    /// </summary>
    public ArMarkerSample Choose(double now, out string change)
    {
        change = null;
        DropFromAnotherClock(now);
        bool currentFresh = m_HasSource && Fresh(now, m_Source, out _);

        // The best fresh sighting other than the current source's.
        bool found = false;
        ulong best = 0;
        float bestQuality = -1f;
        double bestTime = double.NegativeInfinity;
        foreach (var pair in m_Latest)
        {
            if ((m_HasSource && pair.Key == m_Source) || ArMarkerClock.Age(now, pair.Value.time) > staleAfter)
                continue;
            float q = SmoothedQuality(pair.Key);
            if (q > bestQuality)
            {
                found = true;
                best = pair.Key;
                bestQuality = q;
                bestTime = pair.Value.time;
            }
        }

        // A source that has gone quiet hands over sooner when someone else sees the marker: quiet means the other's
        // latest sighting is clearly newer than the source's (sightings from other headsets arrive later, so their
        // age against now would make every one of them look quiet and the source flip back and forth each frame),
        // and not just after a change of source.
        if (currentFresh && found && m_Latest.TryGetValue(m_Source, out var current) &&
            bestTime - current.time > silentFor && now - m_SwitchedAt >= minHold)
            currentFresh = false;

        if (currentFresh)
        {
            if (found && bestQuality > SmoothedQuality(m_Source) + margin)
            {
                if (!m_HasChallenger || m_Challenger != best)
                {
                    m_HasChallenger = true;
                    m_Challenger = best;
                    m_ChallengerSince = now;
                }
                else if (now - m_ChallengerSince >= sustain)
                {
                    change = $"player {best} has been better for {sustain:0.0} s ({bestQuality:0.00} vs {SmoothedQuality(m_Source):0.00})";
                    SwitchTo(best, now);
                }
            }
            else
            {
                m_HasChallenger = false;
            }
        }
        else if (found)
        {
            change = m_HasSource ? $"player {m_Source}'s sightings went stale; player {best} takes over" : $"player {best} sees the marker";
            SwitchTo(best, now);
        }
        else if (m_HasSource)
        {
            change = $"every sighting is stale (last from player {m_Source})";
            m_HasSource = false;
            m_HasChallenger = false;
        }

        if (m_HasSource && m_Latest.TryGetValue(m_Source, out var sample))
        {
            sample.valid = true;
            sample.source = m_Source;
            sample.handoffs = (ushort)Mathf.Min(handoffs, ushort.MaxValue);
            m_Published = sample;
        }
        else
        {
            m_Published.valid = false;
        }
        return m_Published;
    }

    void SwitchTo(ulong client, double now)
    {
        if (m_HasSource && m_Source != client)
            handoffs++;
        m_HasSource = true;
        m_Source = client;
        m_HasChallenger = false;
        m_SwitchedAt = now;
    }
}

/// <summary>
/// Each headset's choice of which pose to draw the cube at: its own sighting when that is fresh, steady and close to
/// the shared pose (it is the most up to date and best aligned with what this headset sees), otherwise the shared
/// pose; the cube hides when both are stale. Switching is held back a little each way, and
/// <see cref="ArMarkerDisplay"/> blends across a switch so the cube doesn't jump.
/// </summary>
public class ArMarkerRenderChooser
{
    public enum Mode { Hidden, Local, Shared }

    public float localFreshFor = 0.2f;
    public float sharedFreshFor = 0.5f;
    public float minLocalQuality = 0.25f;
    public int steadyFrames = 5;
    public float agreeDistance = 0.03f;
    public float agreeAngle = 12f;
    public float toLocalAfter = 0.2f;
    public float toSharedAfter = 0.1f;
    [Tooltip("Poses are carried forward by their velocity at most this long (seconds).")]
    public float extrapolateUpTo = 0.1f;

    ArMarkerSample m_Local, m_LocalPrevious, m_Shared, m_SharedPrevious;
    bool m_HasLocal, m_HasShared;
    int m_Steady;
    double m_LocalGoodSince = double.NaN, m_LocalBadSince = double.NaN;

    public Mode mode { get; private set; } = Mode.Hidden;
    public bool localFresh { get; private set; }
    public bool sharedFresh { get; private set; }
    public bool agree { get; private set; } = true;
    public float disagreement { get; private set; }
    public ArMarkerSample local => m_Local;
    public ArMarkerSample shared => m_Shared;

    /// <summary>Forget every sighting (the clock changed, or the search restarts).</summary>
    public void Reset()
    {
        m_HasLocal = m_HasShared = false;
        m_Local = m_LocalPrevious = m_Shared = m_SharedPrevious = default;
        m_Steady = 0;
        m_LocalGoodSince = m_LocalBadSince = double.NaN;
        mode = Mode.Hidden;
        localFresh = sharedFresh = false;
        agree = true;
        disagreement = 0f;
    }

    /// <summary>A new sighting by this headset.</summary>
    public void AddLocal(ArMarkerSample sample)
    {
        if (m_HasLocal && sample.time <= m_Local.time)
        {
            if (m_Local.time - sample.time < ArMarkerClock.jump)
                return; // out of order
            m_HasLocal = false; // the one kept was stamped on another clock
        }
        bool continuous = m_HasLocal && sample.time - m_Local.time < 0.15 && m_Local.quality >= minLocalQuality;
        m_Steady = sample.quality >= minLocalQuality ? (continuous ? m_Steady + 1 : 1) : 0;
        m_LocalPrevious = m_HasLocal ? m_Local : sample;
        m_Local = sample;
        m_HasLocal = true;
    }

    /// <summary>The shared pose, as last published by the host.</summary>
    public void SetShared(ArMarkerSample sample)
    {
        if (m_HasShared && sample.time == m_Shared.time && sample.valid == m_Shared.valid)
            return;
        // A change of source restarts the velocity estimate.
        m_SharedPrevious = m_HasShared && m_Shared.valid && m_Shared.source == sample.source ? m_Shared : sample;
        m_Shared = sample;
        m_HasShared = true;
    }

    /// <summary>A pose carried forward to <paramref name="time"/> by its recent velocity (at most <see cref="extrapolateUpTo"/>).</summary>
    public Pose Predict(ArMarkerSample latest, ArMarkerSample previous, double time) => Predict(latest, previous, time, float.MaxValue);

    /// <summary>
    /// As <see cref="Predict(ArMarkerSample, ArMarkerSample, double)"/>, carried at most <paramref name="maxSpans"/>
    /// times the gap between the two sightings: two noisy sightings 33 ms apart carried 100 ms ahead triple their noise.
    /// </summary>
    public Pose Predict(ArMarkerSample latest, ArMarkerSample previous, double time, float maxSpans)
    {
        double span = latest.time - previous.time;
        double ahead = Math.Min(Math.Max(time - latest.time, 0.0), extrapolateUpTo);
        if (span < 0.005 || span > 0.3 || ahead <= 0.0)
            return latest.pose;
        float t = Mathf.Min((float)(ahead / span), maxSpans);
        var position = latest.position + (latest.position - previous.position) * t;
        var turn = latest.rotation * Quaternion.Inverse(previous.rotation);
        turn.ToAngleAxis(out float angle, out var axis);
        if (angle > 180f)
            angle -= 360f;
        var rotation = float.IsFinite(axis.x) ? Quaternion.AngleAxis(angle * t, axis) * latest.rotation : latest.rotation;
        return new Pose(position, rotation);
    }

    /// <summary>
    /// Chooses at time <paramref name="now"/> (shared clock); <paramref name="target"/> is the pose to draw, carried
    /// forward to now. <paramref name="self"/> is this headset's network id.
    /// </summary>
    public Mode Update(double now, ulong self, out Pose target)
    {
        localFresh = m_HasLocal && ArMarkerClock.Age(now, m_Local.time) <= localFreshFor;
        sharedFresh = m_HasShared && m_Shared.valid && ArMarkerClock.Age(now, m_Shared.time) <= sharedFreshFor;
        bool steady = localFresh && m_Steady >= steadyFrames && m_Local.quality >= minLocalQuality;

        // Does this headset's sighting agree with the shared pose (compared at the same moment, the shared pose carried
        // forward at most one gap between its sightings so its noise isn't multiplied)?
        agree = true;
        disagreement = 0f;
        if (localFresh && sharedFresh && m_Shared.source != self)
        {
            var sharedThen = Predict(m_Shared, m_SharedPrevious, m_Local.time, 1f);
            float distance = Vector3.Distance(sharedThen.position, m_Local.position);
            float angle = Vector3.Angle(sharedThen.rotation * Vector3.up, m_Local.rotation * Vector3.up);
            disagreement = distance;
            agree = distance <= agreeDistance && angle <= agreeAngle;
        }

        bool localGood = steady && agree;
        if (localGood)
        {
            if (double.IsNaN(m_LocalGoodSince))
                m_LocalGoodSince = now;
            m_LocalBadSince = double.NaN;
        }
        else
        {
            m_LocalGoodSince = double.NaN;
            if (double.IsNaN(m_LocalBadSince))
                m_LocalBadSince = now;
        }

        switch (mode)
        {
            case Mode.Local:
                // Back to the shared pose at once when this headset loses the marker; after a moment when it disagrees.
                if (!localFresh || !steady || (!agree && now - m_LocalBadSince >= toSharedAfter))
                    mode = sharedFresh ? Mode.Shared : (localFresh ? Mode.Local : Mode.Hidden);
                break;
            case Mode.Shared:
                if (localGood && now - m_LocalGoodSince >= toLocalAfter)
                    mode = Mode.Local;
                else if (!sharedFresh)
                    mode = localGood ? Mode.Local : Mode.Hidden;
                break;
            default:
                if (sharedFresh)
                    mode = Mode.Shared;
                else if (localGood && now - m_LocalGoodSince >= toLocalAfter)
                    mode = Mode.Local;
                break;
        }

        target = mode switch
        {
            Mode.Local => Predict(m_Local, m_LocalPrevious, now),
            Mode.Shared => Predict(m_Shared, m_SharedPrevious, now),
            _ => m_HasShared ? m_Shared.pose : m_Local.pose,
        };
        return mode;
    }
}

/// <summary>
/// The pose the cube is drawn at: follows the chosen pose with light smoothing of small jitter (a One Euro filter),
/// and when the choice switches between headsets or between local and shared, carries the difference over and lets
/// it fade out, so the cube glides instead of jumping.
/// </summary>
public class ArMarkerDisplay
{
    public float blendTime = 0.25f;
    public float minCutoff = 2f;   // Hz, at rest
    public float beta = 4f;        // cutoff rises with speed (per m/s)

    Vector3 m_OffsetPosition;
    Quaternion m_OffsetRotation = Quaternion.identity;
    Vector3 m_Position, m_Speed;
    Quaternion m_Rotation = Quaternion.identity;
    bool m_Has;
    string m_Key;

    public Pose pose => new(m_Position, m_Rotation);
    public bool visible => m_Has;

    /// <summary>Forget the last pose (the next one is taken as it is).</summary>
    public void Reset()
    {
        m_Has = false;
        m_Key = null;
    }

    /// <summary>
    /// One frame: <paramref name="target"/> is the chosen pose and <paramref name="key"/> names where it comes from
    /// (a change of key starts a blend).
    /// </summary>
    public Pose Step(Pose target, string key, float deltaTime)
    {
        if (!m_Has)
        {
            m_Position = target.position;
            m_Rotation = target.rotation;
            m_OffsetPosition = Vector3.zero;
            m_OffsetRotation = Quaternion.identity;
            m_Speed = Vector3.zero;
            m_Has = true;
            m_Key = key;
            return pose;
        }

        if (key != m_Key)
        {
            // Keep drawing where it was; the difference fades over blendTime.
            m_OffsetPosition = m_Position - target.position;
            m_OffsetRotation = m_Rotation * Quaternion.Inverse(target.rotation);
            m_Key = key;
        }

        float fade = blendTime > 0f ? Mathf.Exp(-deltaTime * 3f / blendTime) : 0f;
        m_OffsetPosition *= fade;
        m_OffsetRotation = Quaternion.Slerp(Quaternion.identity, m_OffsetRotation, fade);
        var wantPosition = target.position + m_OffsetPosition;
        var wantRotation = m_OffsetRotation * target.rotation;

        // One Euro filter on position; rotation follows with the same responsiveness.
        float dt = Mathf.Max(deltaTime, 1e-4f);
        var rawSpeed = (wantPosition - m_Position) / dt;
        m_Speed = Vector3.Lerp(m_Speed, rawSpeed, Alpha(1f, dt));
        float cutoff = minCutoff + beta * m_Speed.magnitude;
        float alpha = Alpha(cutoff, dt);
        m_Position = Vector3.Lerp(m_Position, wantPosition, alpha);
        m_Rotation = Quaternion.Slerp(m_Rotation, wantRotation, alpha);
        return pose;
    }

    static float Alpha(float cutoff, float dt)
    {
        float tau = 1f / (2f * Mathf.PI * cutoff);
        return 1f / (1f + tau / dt);
    }
}
