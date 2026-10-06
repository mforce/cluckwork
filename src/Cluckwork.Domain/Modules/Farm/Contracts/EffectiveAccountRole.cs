namespace Cluckwork.Domain.Modules.Farm.Contracts;

// #612 — only a plain Worker is ever flock-scoped. Owner, Manager, Sales,
// ReadOnly and Denied all bypass assignment rows entirely.
public enum EffectiveAccountRole
{
    Worker,
    ReadOnly,
    Sales,
    Manager,
    Owner,
    Denied,
}
