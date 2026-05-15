namespace Koshi.Core.Context;

using Koshi.Core.Tokenization;

/// <summary>
/// Manages token budget allocation across context sections.
/// Handles overflow by borrowing from lower-priority roles.
/// </summary>
public sealed class BudgetManager
{
    private readonly ContextBudget _budget;
    private readonly TokenCounter _tokenCounter;
    private readonly Dictionary<ContextRole, int> _allocated;
    private readonly Dictionary<ContextRole, int> _used;

    public BudgetManager(ContextBudget budget, TokenCounter tokenCounter)
    {
        _budget = budget;
        _tokenCounter = tokenCounter;

        // Pre-compute allocations
        _allocated = new Dictionary<ContextRole, int>();
        _used = new Dictionary<ContextRole, int>();

        foreach (var (role, allocation) in budget.Allocations)
        {
            _allocated[role] = allocation.ComputeTokens(budget.AvailableBudget);
            _used[role] = 0;
        }
    }

    /// <summary>Get the allocated budget for a role.</summary>
    public int GetAllocation(ContextRole role) =>
        _allocated.GetValueOrDefault(role, 0);

    /// <summary>Get remaining tokens for a role.</summary>
    public int GetRemaining(ContextRole role) =>
        Math.Max(0, GetAllocation(role) - _used.GetValueOrDefault(role, 0));

    /// <summary>Get total remaining budget across all roles.</summary>
    public int TotalRemaining => _budget.AvailableBudget - _used.Values.Sum();

    /// <summary>
    /// Try to reserve tokens for a section. Returns true if the section fits
    /// within its role's budget (or can borrow from overflow pool).
    /// </summary>
    public bool TryReserve(ContextRole role, int tokens)
    {
        int remaining = GetRemaining(role);
        if (tokens <= remaining)
        {
            _used[role] = _used.GetValueOrDefault(role, 0) + tokens;
            return true;
        }

        // Try borrowing from global overflow (unused tokens from other roles)
        int overflow = TotalRemaining;
        if (tokens <= overflow)
        {
            _used[role] = _used.GetValueOrDefault(role, 0) + tokens;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Force-reserve tokens (used for mandatory sections like system prompt and user query).
    /// </summary>
    public void ForceReserve(ContextRole role, int tokens)
    {
        _used[role] = _used.GetValueOrDefault(role, 0) + tokens;
    }

    /// <summary>Get a snapshot of current allocations and usage.</summary>
    public IReadOnlyDictionary<ContextRole, int> GetUsageSnapshot() =>
        new Dictionary<ContextRole, int>(_used);

    /// <summary>
    /// Rebalance: redistribute unused budget from roles that have leftover
    /// to roles that need more. Call after mandatory sections are placed.
    /// </summary>
    public void Rebalance()
    {
        // Find roles with surplus and roles that could use more
        int totalSurplus = 0;
        var deficitRoles = new List<(ContextRole Role, int Deficit)>();

        foreach (var (role, allocated) in _allocated)
        {
            int used = _used.GetValueOrDefault(role, 0);
            if (used < allocated)
                totalSurplus += allocated - used;
        }

        // Redistribute surplus proportionally to RetrievedContext and Memory
        // (these are the roles that benefit most from extra budget)
        if (totalSurplus > 0)
        {
            var expandableRoles = new[] { ContextRole.RetrievedContext, ContextRole.Memory };
            int perRole = totalSurplus / expandableRoles.Length;

            foreach (var role in expandableRoles)
            {
                int maxForRole = _budget.Allocations.TryGetValue(role, out var alloc)
                    ? alloc.MaxTokens : int.MaxValue;
                int currentAlloc = _allocated.GetValueOrDefault(role, 0);
                int expansion = Math.Min(perRole, maxForRole - currentAlloc);
                _allocated[role] = currentAlloc + expansion;
            }
        }
    }
}
