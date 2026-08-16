# Release Orchestrator - Локализация

> [English version ->](../en/localization.md) - [← Вернуться к документации](../README.md)

---

## Обзор

Release Orchestrator поддерживает **несколько языков** с локализацией для UI и API ответов. Сейчас поставляется с **английским** и **русским**.

Этот документ объясняет:
- Как работает локализация
- Как добавить новый язык
- Что локализуется, а что нет
- Best practices для переводчиков и разработчиков

---

## Как устроена локализация

### Технологический стек

- **.resx файлы** - XML-ресурсы для каждого языка (.NET стандарт)
- **Пространства ресурсов:**
  - `ReleaseOrchestrator.Pwa.Resources.UiStrings` - UI строки (PWA)
  - `ReleaseOrchestrator.Web.Resources.ApiStrings` - API ошибки/статусы
- **Выбор языка:**
  - PWA: `LanguageService` (localStorage-backed, fallback на предпочтение браузера)
  - API: `AcceptLanguageHandler` (читает заголовок `Accept-Language`)
- **.NET Culture:** Использует стандартный `CultureInfo` (например, `en`, `ru`)

### Локализация UI (PWA)

PWA (Blazor WebAssembly) сохраняет выбор языка в localStorage браузера и применяет его перед первым рендером.

**Пример компонента:**
```csharp
@using ReleaseOrchestrator.Pwa.Resources

<button>@UiStrings.SaveButton</button>
<p>@UiStrings.ReleasePlanDescription</p>
```

**Поддерживаемые культуры:**
```csharp
public static readonly IReadOnlyList<(string Code, string DisplayName)> SupportedCultures =
[
    ("en", "English"),
    ("ru", "Русский")
];
```

### Локализация API (Web)

REST API ответы локализуются на основе заголовка `Accept-Language`.

**Пример:**
```
GET /api/merge-requests HTTP/1.1
Accept-Language: ru;q=0.9, en;q=0.8
```

Ошибка ответа будет на русском, если доступна, иначе на английском.

**Реализация:** `AcceptLanguageHandler` middleware парсит заголовок и устанавливает `CultureInfo.CurrentUICulture` для запроса.

---

## Ресурсные файлы

### Структура файлов

```
src/ReleaseOrchestrator.Pwa/
  Resources/
    UiStrings.resx              # Английский (по умолчанию)
    UiStrings.ru.resx           # Русский
    
src/ReleaseOrchestrator.Web/
  Resources/
    ApiStrings.resx             # Английский (по умолчанию)
    ApiStrings.ru.resx          # Русский
```

### Формат: `.resx` файлы

Каждый файл - это XML. Пример (`UiStrings.resx`):

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

Эквивалентный русский файл (`UiStrings.ru.resx`):

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

**Ключевые правила:**
- Имя ресурса (например, `SaveButton`) должно быть **идентичным** во всех языковых файлах
- Значение - это переведённая строка
- XML чувствителен к регистру; всегда используйте точные имена

---

## Как добавить новый язык

### Шаг 1: Создайте ресурсные файлы

1. **Для UI:** Скопируйте `src/ReleaseOrchestrator.Pwa/Resources/UiStrings.resx` в `UiStrings.{cultureCode}.resx`
   - Пример: `UiStrings.es.resx` для испанского
   
2. **Для API:** Скопируйте `src/ReleaseOrchestrator.Web/Resources/ApiStrings.resx` в `ApiStrings.{cultureCode}.resx`

3. **Переведите каждое значение** в обоих файлах

**Используйте коды культур:**
- `en` - английский
- `es` - испанский
- `fr` - французский
- `de` - немецкий
- `ru` - русский
- `zh` - китайский
- `ja` - японский

[Полный список кодов культур](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo)

### Шаг 2: Зарегистрируйте в `LanguageService`

Отредактируйте `src/ReleaseOrchestrator.Pwa/Services/LanguageService.cs`:

```csharp
public static readonly IReadOnlyList<(string Code, string DisplayName)> SupportedCultures =
[
    ("en", "English"),
    ("es", "Español"),           // Добавьте это
    ("ru", "Русский")
];
```

### Шаг 3: Протестируйте сборку

```bash
dotnet build
# Должно успешно собраться с 0 ошибками
# Убедитесь, что оба файла ресурсов загружаются без warning'ов
```

### Шаг 4: Протестируйте в PWA

```bash
dotnet run --project src/ReleaseOrchestrator.Web
```

Перейдите в приложение, откройте switcher языков (внизу справа), и убедитесь, что новый язык появляется и отображается корректно.

### Шаг 5: Проверьте API

```bash
curl -H "Accept-Language: es" https://localhost:5173/api/health/ready
```

Если приложение вернёт ошибку на испанском в API ответах, локализация работает.

---

## Что локализуется

### UI Строки (Всегда локализуются)

- **Надписи на кнопках:** Save, Cancel, Delete, Create, Edit
- **Заголовки страниц и heading'и:** "Release Plans", "Repositories", "Permissions"
- **Placeholder'ы форм и справочный текст**
- **Сообщения валидации:** "This field is required", "Invalid email"
- **Badges статуса:** "Merged", "Opened", "Closed"
- **Пункты навигационного меню**
- **Заголовки диалогов и текст тела**
- **Сообщения об ошибках, показываемые пользователю**

### API Ответы (Частично локализованы)

- **Ошибки валидации:** "Email is invalid" -> "Email не валиден"
- **Сообщения конфликтов:** "This plan conflicts with another" -> "Этот план конфликтует с другим"
- **HTTP status описания** (в JSON error bodies)

### НЕ локализуется (Намеренно)

Это **всегда английский**, даже в русском UI:

- **Логи приложения** - Операторы должны понимать английские логи
- **Имена полей БД и коды ошибок** - Консистентность через деплои
- **Документация и комментарии в коде**
- **Имена HTTP заголовков** (стандартные, должны быть английские)
- **Интерфейсы аутентификации/OIDC провайдера** - Внешние

**Обоснование:** Логи операционные, не пользовательские. Смешивание языков в логах затрудняет troubleshooting.

---

## Best Practices

### Для переводчиков

1. **Держите переводы лаконичными** - UI пространство ограничено
2. **Соблюдайте согласование слов** - Соответствуйте стилю английского оригинала
3. **Добавляйте контекстные notes** - Если слово может иметь несколько значений, добавьте комментарий
4. **Никогда не меняйте имена ресурсов** - Имя (ключ) остаётся тем же; переводите только значение
5. **Тестируйте в приложении** - Некоторые строки могут не вместиться в UI на переведённом языке

### Для разработчиков

1. **Всегда используйте ресурс-строки, никогда не hardcodе-кодируйте текст**
   ```csharp
   // Плохо
   <p>Release Plan</p>
   
   // Хорошо
   <p>@UiStrings.ReleasePlanTitle</p>
   ```

2. **Используйте консистентные имена** - Если "Deploy" используется в одном месте, используйте `ReleasePlanDeploy` консистентно

3. **Добавляйте context comments в .resx**
   ```xml
   <data name="StageLabel" xml:space="preserve">
     <comment>Label for a single stage in the release plan, e.g. "Stage 1"</comment>
     <value>Stage</value>
   </data>
   ```

4. **Не встраивайте пользовательский текст в код**
   ```csharp
   // Плохо
   throw new InvalidOperationException($"Plan {name} does not exist");
   
   // Хорошо (если применимо)
   throw new InvalidOperationException(ApiStrings.PlanNotFound);
   ```

5. **Тестируйте новые строки** - Добавьте unit-тест для проверки существования ресурса перед ship'ом

---

## Текущий статус

| Язык | UI Strings | API Strings | Протестировано |
|---|---|---|---|
| Английский | 129 ключей | 32 ключа | ✓ (fallback язык) |
| Русский | 129 ключей | 32 ключа | ✓ (полный перевод) |
| Испанский | - | - | - |
| Французский | - | - | - |

---

## Решение проблем

### "String key not found" Ошибка в UI

- **Причина:** Опечатка в имени ресурса (case-sensitive)
- **Решение:** Проверьте `UiStrings.resx` для точного написания, например, `SaveButton` не `save_button`

### UI показывает английский даже при установке русского языка

- **Причина 1:** Кэш браузера (localStorage не очищен)
  - Решение: Очистите localStorage, перезагрузите
  
- **Причина 2:** Ресурс-файл не развёрнут
  - Решение: Пересоберите и переразвёрните
  
- **Причина 3:** Опечатка кода культуры в `SupportedCultures`
  - Решение: Проверьте точное совпадение кода

### API возвращает английский despite `Accept-Language: ru`

- **Причина:** API строка не определена в русском ресурсе
- **Решение:** Добавьте отсутствующий ключ в `ApiStrings.ru.resx`

---

## Детали реализации

### LanguageService (PWA)

```csharp
// Разрешает сохранённый язык -> предпочтение браузера -> английский fallback
Apply(Resolve(stored) ?? Resolve(CultureInfo.CurrentUICulture.Name) ?? "en");

// Raises LanguageChanged event при смене культуры
public async Task SetCultureAsync(string cultureCode)
{
    Apply(cultureCode);
    await js.InvokeVoidAsync("localStorage.setItem", StorageKey, cultureCode);
    LanguageChanged?.Invoke();
}
```

### AcceptLanguageHandler (Web)

```csharp
// Парсит Accept-Language заголовок, устанавливает CultureInfo для запроса
var supportedLanguages = new[] { "en", "ru", "es", ... };
var preferred = ParseAcceptLanguage(request.Headers["Accept-Language"], supportedLanguages);
CultureInfo.CurrentUICulture = new CultureInfo(preferred ?? "en");
```

---

## См. также

- [Конфигурация](configuration.md) - Настройки приложения
- [Архитектура](architecture.md) - Дизайн системы
- [Начало работы](getting-started.md) - Первые шаги

---

## Ресурсы

- [Microsoft .NET Globalization and Localization Documentation](https://learn.microsoft.com/en-us/dotnet/standard/globalization-localization/)
- [IETF Language Tags](https://tools.ietf.org/html/rfc5646)
- [Culture Codes Reference](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo)
