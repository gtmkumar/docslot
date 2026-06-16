namespace mediq.SharedDataModel.Docslot.Navigation;

/// <summary>
/// One node in the backend-driven navigation tree.
/// <para>
/// Mirrors the rows returned by <c>platform.get_user_menus(p_user_id, p_tenant_id,
/// p_tenant_type, p_product_key)</c> (database/08_rbac_navigation.sql), which projects
/// <c>platform.navigation_menus</c> already filtered by the user's effective permissions
/// (via <c>menu_permissions</c>) AND the tenant's type. The function returns a FLAT,
/// ordered list of <c>(menu_id, parent_menu_id, menu_key, menu_label, menu_label_hi,
/// menu_icon, menu_url, display_order, is_section_header, badge_source)</c>; the API
/// assembles those rows into this tree (parent → children) before returning it.
/// </para>
/// <para>
/// Per CLAUDE.md/REACT_SKILL.md: the frontend renders this tree as-is and NEVER branches
/// on role in JSX — visibility is already decided server-side. Bilingual: both
/// <see cref="Label"/> (en) and <see cref="LabelHi"/> (hi) are carried so the UI can switch
/// language without another round-trip.
/// </para>
/// </summary>
/// <param name="Id">Maps to <c>navigation_menus.menu_id</c> (UUID).</param>
/// <param name="ParentId">Maps to <c>navigation_menus.parent_menu_id</c>; null at the root.</param>
/// <param name="Key">
/// Stable dotted key, e.g. <c>bookings.today</c>. Maps to <c>navigation_menus.menu_key</c>.
/// The frontend keys off this, not the label.
/// </param>
/// <param name="Label">English label. Maps to <c>navigation_menus.menu_label</c> (NOT NULL).</param>
/// <param name="LabelHi">Hindi label. Maps to <c>navigation_menus.menu_label_hi</c> (nullable).</param>
/// <param name="Icon">Frontend icon identifier. Maps to <c>navigation_menus.menu_icon</c> (nullable).</param>
/// <param name="Route">
/// Route path, e.g. <c>/bookings/today</c>. Maps to <c>navigation_menus.menu_url</c> (nullable —
/// section headers have no route).
/// </param>
/// <param name="SortOrder">Sibling ordering. Maps to <c>navigation_menus.display_order</c> (INT).</param>
/// <param name="IsSectionHeader">
/// True for non-clickable group labels. Maps to <c>navigation_menus.is_section_header</c>.
/// </param>
/// <param name="BadgeSource">
/// Optional badge feed key, e.g. <c>pending_bookings_count</c>. Maps to
/// <c>navigation_menus.badge_source</c> (nullable). The frontend resolves this key to a live
/// count (same source as <see cref="Dashboard.Dtos.DashboardSummaryDto.LiveQueueCount"/>).
/// </param>
/// <param name="Children">
/// Child nodes, assembled from rows whose <c>parent_menu_id</c> equals this node's
/// <see cref="Id"/>, ordered by <see cref="SortOrder"/>. Empty (not null) for leaves.
/// </param>
public sealed record MenuNodeDto(
    Guid Id,
    Guid? ParentId,
    string Key,
    string Label,
    string? LabelHi,
    string? Icon,
    string? Route,
    int SortOrder,
    bool IsSectionHeader,
    string? BadgeSource,
    IReadOnlyList<MenuNodeDto> Children);
