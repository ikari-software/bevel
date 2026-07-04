namespace Bevel.Core;

/// <summary>
/// Product identity, frozen at M0 (2026-07-04) per ARCH-01. These identifiers are
/// load-bearing (TCC grants key on bundle id + signing identity; the scheme is an
/// integration contract) and must not drift.
/// </summary>
public static class AppInfo
{
    public const string ProductName = "Bevel";
    public const string BundleId = "pl.ikari.bevel";
    public const string UrlScheme = "bevel";
    public const string Milestone = "M0 — Bootstrap";
}
