# Echelon - Localization

> [Русская версия ->](../ru/localization.md) - [← Back to docs](../README.md)

---

## Overview

Echelon supports **multiple languages** with localization for both UI and API responses. Currently shipped with **English** and **Russian**.

This document explains:
- How localization is implemented
- How to add a new language
- What is and isn't localized
- Best practices for translators and developers

---

## How Localization Works

### Technology Stack

- **.resx files** - XML resource files for each language (.NET standard)
- **Resource namespaces:**
  - `Echelon.Pwa.Resources.UiStrings` - UI strings (PWA)
  - `Echelon.Web.Resources.ApiStrings` - API error/status messages
- **Language selection:**
  - PWA: `LanguageService` (localStorage-backed, browser preference fallback)
  - API: `AcceptLanguageHandler` (reads `Accept-Language` header)
- **.NET Culture:** Uses standard `CultureInfo` (e.g., `en`, `ru`)

### UI Localization (PWA)

The PWA (Blazor WebAssembly) stores language preference in browser localStorage and applies it before first render.

**Component example:**
```csharp
@using Echelon.Pwa.Resources

<button>@UiStrings.SaveButton</button>
<p>@UiStrings.ReleasePlanDescription</p>
```

**Supported cultures:**
```csharp
public static readonly IReadOnlyList<(string Code, string DisplayName)> SupportedCultures =
[
    ("en", "English"),
    ("ru", "Русский")
];
```

### API Localization (Web)

REST API responses are localized based on the `Accept-Language` header.

**Example:**
```
GET /api/merge-requests HTTP/1.1
Accept-Language: ru;q=0.9, en;q=0.8
```

Response error will be in Russian if available, otherwise English.

**Implementation:** `AcceptLanguageHandler` middleware parses the header and sets `CultureInfo.CurrentUICulture` for the request.

---

## Resource Files

### File Structure

```
src/Echelon.Pwa/
  Resources/
    UiStrings.resx              # English (default)
    UiStrings.ru.resx           # Russian
    
src/Echelon.Web/
  Resources/
    ApiStrings.resx             # English (default)
    ApiStrings.ru.resx          # Russian
```

### Format: `.resx` Files

Each file is XML. Example (`UiStrings.resx`):

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <!-- ... -->
  <data name="SaveButton" xml:space="preserve">
    <value>Save</value>
  </data>
  <data name="ReleasePlanDescription" xml:space="preserve">
    <value>A staged deployment plan for merge requests</value>
  </data>
  <!-- ... -->
</root>
```

Equivalent Russian file (`UiStrings.ru.resx`):

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <!-- ... -->
  <data name="SaveButton" xml:space="preserve">
    <value>Сохранить</value>
  </data>
  <data name="ReleasePlanDescription" xml:space="preserve">
    <value>Упорядоченный по стадиям план деплоя merge request'ов</value>
  </data>
  <!-- ... -->
</root>
```

**Key rules:**
- Resource name (e.g., `SaveButton`) must be **identical** across all language files
- Value is the translated string
- XML is case-sensitive; always use exact names

---

## How to Add a New Language

### Step 1: Create Resource Files

1. **For UI:** Copy `src/Echelon.Pwa/Resources/UiStrings.resx` to `UiStrings.{cultureCode}.resx`
   - Example: `UiStrings.es.resx` for Spanish
   
2. **For API:** Copy `src/Echelon.Web/Resources/ApiStrings.resx` to `ApiStrings.{cultureCode}.resx`

3. **Translate every value** in both files

**Use culture codes:**
- `en` - English
- `es` - Spanish
- `fr` - French
- `de` - German
- `ru` - Russian
- `zh` - Chinese
- `ja` - Japanese

[Full list of culture codes](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo)

### Step 2: Register in `LanguageService`

Edit `src/Echelon.Pwa/Services/LanguageService.cs`:

```csharp
public static readonly IReadOnlyList<(string Code, string DisplayName)> SupportedCultures =
[
    ("en", "English"),
    ("es", "Español"),           // Add this
    ("ru", "Русский")
];
```

### Step 3: Test Build

```bash
dotnet build
# Should succeed with 0 errors
# Verify both resource files load without warnings
```

### Step 4: Test in PWA

```bash
dotnet run --project src/Echelon.Web
```

Navigate to the app, open language switcher (bottom-right), and verify the new language appears and displays correctly.

### Step 5: Verify API

```bash
curl -H "Accept-Language: es" https://localhost:5173/api/health/ready
```

If the app returns an error in Spanish API responses, localization is working.

---

## What Is Localized

### UI Strings (Always Localized)

- **Button labels:** Save, Cancel, Delete, Create, Edit
- **Page titles and headings:** "Release Plans", "Repositories", "Permissions"
- **Form placeholders and help text**
- **Validation messages:** "This field is required", "Invalid email"
- **Status badges:** "Merged", "Opened", "Closed"
- **Navigation menu items**
- **Dialog titles and body text**
- **Error messages shown to users**

### API Responses (Partially Localized)

- **Validation errors:** "Email is invalid" -> "Email не валиден"
- **Conflict messages:** "This plan conflicts with another" -> "Этот план конфликтует с другим"
- **HTTP status descriptions** (in JSON error bodies)

### NOT Localized (Intentional)

These are **always English**, even in Russian UI:

- **Application logs** - Operators are expected to understand English logs
- **Database field names and error codes** - Consistency across deployments
- **Documentation and comments in code**
- **HTTP header names** (standard, must be English)
- **Authentication/OIDC provider interfaces** - External

**Rationale:** Logs are operational, not user-facing. Mixing languages in logs makes troubleshooting harder across teams.

---

## Best Practices

### For Translators

1. **Keep translations concise** - UI space is limited
2. **Maintain noun/verb agreement** - Match the style of the English original
3. **Use context notes** - If a word can have multiple meanings, add a comment
4. **Never change resource names** - The name (key) stays the same; only translate the value
5. **Test in the app** - Some strings might not fit in the UI layout in translated form

### For Developers

1. **Always use resource strings, never hardcoded text**
   ```csharp
   // Bad
   <p>Release Plan</p>
   
   // Good
   <p>@UiStrings.ReleasePlanTitle</p>
   ```

2. **Use consistent naming** - If "Deploy" is used in one place, use `ReleasePlanDeploy` consistently

3. **Add context comments in .resx**
   ```xml
   <data name="StageLabel" xml:space="preserve">
     <comment>Label for a single stage in the release plan, e.g. "Stage 1"</comment>
     <value>Stage</value>
   </data>
   ```

4. **Don't embed user-facing text in code**
   ```csharp
   // Bad
   throw new InvalidOperationException($"Plan {name} does not exist");
   
   // Good (if applicable)
   throw new InvalidOperationException(ApiStrings.PlanNotFound);
   ```

5. **Test new strings** - Add a unit test to verify the resource exists before shipping

---

## Current Status

| Language | UI Strings | API Strings | Tested |
|---|---|---|---|
| English | 129 keys | 32 keys | ✓ (fallback language) |
| Russian | 129 keys | 32 keys | ✓ (complete translation) |
| Spanish | - | - | - |
| French | - | - | - |

---

## Troubleshooting

### "String key not found" Error in UI

- **Cause:** Resource name typo (case-sensitive)
- **Solution:** Check `UiStrings.resx` for exact spelling, e.g., `SaveButton` not `save_button`

### UI shows English even when language is set to Russian

- **Cause 1:** Browser cache (localStorage not cleared)
  - Solution: Clear localStorage, reload
  
- **Cause 2:** Resource file not deployed
  - Solution: Rebuild and redeploy
  
- **Cause 3:** Culture code typo in `SupportedCultures`
  - Solution: Verify code matches exactly

### API returns English error despite `Accept-Language: ru`

- **Cause:** API string not defined in Russian resource
- **Solution:** Add the missing key to `ApiStrings.ru.resx`

---

## Implementation Details

### LanguageService (PWA)

```csharp
// Resolves saved language -> browser preference -> English fallback
Apply(Resolve(stored) ?? Resolve(CultureInfo.CurrentUICulture.Name) ?? "en");

// Raises LanguageChanged event when culture switches
public async Task SetCultureAsync(string cultureCode)
{
    Apply(cultureCode);
    await js.InvokeVoidAsync("localStorage.setItem", StorageKey, cultureCode);
    LanguageChanged?.Invoke();
}
```

### AcceptLanguageHandler (Web)

```csharp
// Parses Accept-Language header, sets CultureInfo for the request
var supportedLanguages = new[] { "en", "ru", "es", ... };
var preferred = ParseAcceptLanguage(request.Headers["Accept-Language"], supportedLanguages);
CultureInfo.CurrentUICulture = new CultureInfo(preferred ?? "en");
```

---

## See Also

- [Configuration](configuration.md) - Application settings
- [Architecture](architecture.md) - System design
- [Getting Started](getting-started.md) - First steps

---

## Resources

- [Microsoft .NET Globalization and Localization Documentation](https://learn.microsoft.com/en-us/dotnet/standard/globalization-localization/)
- [IETF Language Tags](https://tools.ietf.org/html/rfc5646)
- [Culture Codes Reference](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo)
