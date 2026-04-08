using Atlas.Api.Models;
using Atlas.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.Api.Controllers;

[ApiController]
[Route("api/experimental/runtime")]
public sealed class ExperimentalRuntimeController : ControllerBase
{
    private const string BotKey = "MULTI";

    private readonly AtlasRuntimeContext _runtime;
    private readonly IBotStateRepository _stateRepository;
    private readonly IBotLeaderElectionService _leaderElection;

    public ExperimentalRuntimeController(
        AtlasRuntimeContext runtime,
        IBotStateRepository stateRepository,
        IBotLeaderElectionService leaderElection)
    {
        _runtime = runtime;
        _stateRepository = stateRepository;
        _leaderElection = leaderElection;
    }

    [HttpGet]
    public ActionResult<BotRuntimeStatusSnapshot> GetRuntime()
    {
        BotStateRecord? state = _stateRepository.Load(BotKey);
        BotLeaderLeaseSnapshot leader = _leaderElection.GetSnapshot(BotKey, _runtime);

        return Ok(new BotRuntimeStatusSnapshot(
            BotKey: BotKey,
            RuntimeRole: _runtime.Role,
            InstanceId: _runtime.InstanceId,
            HostName: _runtime.HostName,
            RepositoryBackend: _stateRepository.BackendName,
            LeaderBackend: _leaderElection.BackendName,
            CanRunBotLoop: _runtime.CanRunBotLoop,
            Leader: leader,
            StateVersion: state?.StateVersion ?? 0,
            LastPersistedAt: state?.LastPersistedAt,
            LastEvaluationAt: state?.LastEvaluationAt,
            LastCycleStatus: state?.LastCycleStatus ?? "cold",
            LastCycleDurationMs: state?.LastCycleDurationMs ?? 0));
    }

    [HttpGet("leader")]
    public ActionResult<BotLeaderLeaseSnapshot> GetLeader() =>
        Ok(_leaderElection.GetSnapshot(BotKey, _runtime));

    [HttpGet("health")]
    public ActionResult<BotRuntimeHealthSnapshot> GetHealth()
    {
        BotStateRecord? state = _stateRepository.Load(BotKey);
        BotLeaderLeaseSnapshot leader = _leaderElection.GetSnapshot(BotKey, _runtime);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string status = state is null
            ? "cold"
            : leader.LeaseUntil is not null && leader.LeaseUntil > now
                ? state.LastCycleStatus.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
                  state.LastCycleStatus.Equals("trading", StringComparison.OrdinalIgnoreCase) ||
                  state.LastCycleStatus.Equals("idle", StringComparison.OrdinalIgnoreCase)
                    ? "healthy"
                    : state.LastCycleStatus
                : "standby";

        return Ok(new BotRuntimeHealthSnapshot(
            BotKey: BotKey,
            Status: status,
            RuntimeRole: _runtime.Role,
            IsLeader: leader.IsLeader,
            LeaderInstanceId: leader.OwnerInstanceId,
            LeaseUntil: leader.LeaseUntil,
            LastPersistedAt: state?.LastPersistedAt,
            LastEvaluationAt: state?.LastEvaluationAt,
            LastCycleStatus: state?.LastCycleStatus ?? "cold",
            LastCycleDurationMs: state?.LastCycleDurationMs ?? 0,
            Timestamp: now));
    }
}
