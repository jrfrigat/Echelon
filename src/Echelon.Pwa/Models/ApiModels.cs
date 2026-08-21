using Echelon.Application.DTOs;
using Echelon.Core.Enums;
using Echelon.Providers.Abstractions;

namespace Echelon.Pwa.Models;

// What is left here is the shape of a response the API builds itself - a controller projection with no
// type behind it. Everything the server already declares as a DTO is USED from
// <see cref="Echelon.Application.DTOs"/> rather than copied: a copy is a promise nobody checks, and the
// one that drifted (a plan version that became an int on the server and stayed a string here) took
// down every plan the UI tried to read.
//
// A field the API declares as an enum is an enum here too, which works because both ends read and write
// the enum's name - the API registers JsonStringEnumConverter, and so does ApiService. A closed set the
// server owns is never re-spelled here as a string literal: the compiler cannot check "Poll", and a
// rename would leave the UI quietly comparing against nothing.


// ---- ordering rules (mirrors PlanningController) ----

/// <summary>The ordering-rule document, as text.</summary>
public record OrderingRulesDocumentDto(string Document);

/// <summary>What checking a document found.</summary>
/// <param name="IsValid">Whether it would be accepted.</param>
/// <param name="Problems">Everything wrong with it; empty when valid.</param>
/// <param name="Groups">
/// What each group selects right now. The useful half: a document can be perfectly valid and select
/// nothing, since a glob with a typo is still a well-formed glob.
/// </param>
public record OrderingRulesValidationDto(
    bool IsValid, List<string> Problems, List<OrderingRuleGroupMatchDto> Groups);

/// <summary>What one group currently selects.</summary>
public record OrderingRuleGroupMatchDto(string Group, int Matched, List<string> Examples);


/// <summary>A merge request an operator forced into or out of a task's rollout.</summary>
public record PlanMembershipDto(
    Guid MergeRequestId, string MrExternalId, string RepositoryName,
    string SourceBranch, string MrStatus, string State);

/// <summary>A named selector in the ordering rules, as the visual editor holds it.</summary>
/// <remarks>Mutable, unlike the read DTOs: this one is bound to form fields.</remarks>
public class OrderingRuleGroupDto
{
    public string Name { get; set; } = "";
    public List<string> Connectors { get; set; } = [];
    public List<string> Repositories { get; set; } = [];
    public List<string> Branches { get; set; } = [];
    public List<string> TaskKeys { get; set; } = [];
    public List<string> Labels { get; set; } = [];
}

/// <summary>One ordering rule, as the visual editor holds it.</summary>
public class OrderingRuleOrderDto
{
    public string Group { get; set; } = "";
    public List<string> Needs { get; set; } = [];
    public string Type { get; set; } = "Hard";
    public string Scope { get; set; } = "AcrossPlan";
}

/// <summary>The ordering rules as a structure.</summary>
/// <param name="Editable">
/// False when the stored document says something the form cannot express, so the form must not own it.
/// </param>
public record OrderingRulesModelDto(
    bool Editable,
    List<string> Problems,
    List<OrderingRuleGroupDto> Groups,
    List<OrderingRuleOrderDto> Order);

