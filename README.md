# ACD · Asistente de Conexión Documental

Agente de escritorio para Windows que conecta las aplicaciones web del SGD con el equipo del usuario mediante un servidor WebSocket local. Cubre dos frentes:

- **Firma digital** con [FirmaONPE](https://www.gob.pe/onpe) para el MFD (Módulo de Firma Documental).
- **Espacio de trabajo del documento** para el MPD (Módulo de Proyección de Documentos): escribir, leer, vigilar, convertir, restaurar y descartar el Word de un remito en su carpeta local.

## Funcionamiento

ACD corre como **aplicación de bandeja del sistema** y escucha en `localhost:7272`. En una firma desde el MFD:

1. El navegador se conecta por WebSocket y envía el PDF.
2. ACD deja el archivo en la carpeta vigilada `C:\TFIRMA`, de donde lo toma FirmaONPE.
3. FirmaONPE firma con el certificado digital del usuario.
4. ACD detecta el archivo firmado y lo devuelve al navegador.
5. El navegador sube el PDF firmado al backend, que valida la firma y lo registra en el SGD.

El agente se inicia bajo demanda con el esquema URI `acd://`, registrado en `HKCU\Software\Classes`, sin privilegios de administrador.

## Protocolo WebSocket

Toda sesión empieza con `CONNECTED` (lo envía ACD), seguido de `AUTH` → `AUTH_OK`. `CONNECTED.capabilities` anuncia las operaciones disponibles; `protocolVersion` es `2`.

| Capability | Operaciones |
|------------|-------------|
| `pdf.sign.firma-onpe` | Firma con FirmaONPE |
| `pdf.open` | Apertura de PDF |
| `document.workspace` | `WRITE_WORD`, `READ_WORD`, `CONVERT_TO_PDF`, `WRITE_PDF_COPY`, `OPEN_FOLDER` |
| `document.workspace.watch` | `WATCH_WORKSPACE` |
| `document.workspace.restore.close` | `RESTORE_WORD` |
| `document.workspace.discard` | `DISCARD_WORD` |

Una sesión ejecuta un solo tipo de operación y ACD admite una operación activa por tipo; una segunda recibe `SESSION_BUSY` y cierre `4002`. Las sesiones de vigilancia son la excepción y no ocupan cupo. Todo `ERROR` (`code`, `message`, `category`) es terminal: ACD cierra el socket a continuación. Las condiciones recuperables usan mensajes propios.

### Espacio de trabajo del documento (`document.workspace`)

Lee y escribe el Word y la copia PDF de un remito en su carpeta del equipo, y convierte un `.docx` a PDF con Microsoft Word. Cada comando es una sesión: `AUTH` → `AUTH_OK`, el comando, su respuesta y cierre `1000`.

**Carpeta del remito.** `<raíz>\<anio>\<numeroEmision>`. La raíz por defecto es `Documentos\TDOCUMENTOS\MPD` (`SpecialFolder.MyDocuments`, respeta la redirección a OneDrive) y se cambia con `Acd:DocumentWorkspace:RootDirectory`, que expande variables de entorno. `anio` lleva 4 dígitos y `numeroEmision` de 1 a 10, usado tal como llega, con ceros a la izquierda; otro formato devuelve `WORKSPACE_INVALID_KEY`.

**Nombre de archivo.** `filename` debe ser un nombre seguro: sin rutas ni `..`, hasta 180 caracteres y con la extensión exacta.

**Word del remito.** Es el `*.docx` de la carpeta con la fecha efectiva más reciente, `changedAt = max(CreationTime, LastWriteTime)`, sin contar los archivos de bloqueo `~$*`. Así, un Word pegado en la carpeta pasa a ser el activo aunque su `LastWriteTime` sea anterior. En empate gana el nombre que ordena último (ordinal, sin distinguir mayúsculas), que corresponde al sufijo `_<YYYYMMDD-HHMMSS>` más reciente.

**Contenido binario.** El JSON declara `size` (de 1 a `Acd:DocumentWorkspace:MaxFileBytes`, 20 MiB por defecto) y `sha256` (64 caracteres hexadecimales); el frame binario va justo después. Las fechas son hora local sin zona (`yyyy-MM-ddTHH:mm:ss`).

| Dirección | Mensaje | Campos |
|-----------|---------|--------|
| Web → ACD | `WRITE_WORD` + 1 frame binario | `requestId` (UUID), `anio`, `numeroEmision`, `filename`, `size`, `sha256`, `open` |
| ACD → Web | `WORD_WRITTEN` | `requestId`, `filename`, `changedAt`, `opened` |
| Web → ACD | `READ_WORD` | `requestId`, `anio`, `numeroEmision`, `filename` |
| ACD → Web | `WORD_CONTENT` + 1 frame binario | `requestId`, `filename`, `size`, `sha256`, `changedAt` |
| Web → ACD | `CONVERT_TO_PDF` + 1 frame binario | `requestId`, `size`, `sha256` |
| ACD → Web | `PDF_CONTENT` + 1 frame binario | `requestId`, `size`, `sha256` |
| Web → ACD | `WRITE_PDF_COPY` + 1 frame binario | `requestId`, `anio`, `numeroEmision`, `wordFilename`, `size`, `sha256` |
| ACD → Web | `PDF_COPY_WRITTEN` | `requestId`, `filename`, `renamed` |
| Web → ACD | `OPEN_FOLDER` | `requestId`, `anio`, `numeroEmision` |
| ACD → Web | `FOLDER_OPENED` | `requestId` |

1. `WRITE_WORD` crea el archivo con `CreateNew` y nunca sobrescribe: si el nombre existe, responde `WORKSPACE_FILE_EXISTS`. Crea la carpeta si falta. Con `open: true` abre el archivo con la aplicación predeterminada de Windows y lo informa en `opened`, que también es `false` cuando `open` es `false`. Si la apertura falla, el archivo queda en la carpeta y no hay `ERROR`.
2. `READ_WORD` abre el archivo compartiendo lectura y borrado, así que funciona con el documento abierto en Word. Un `.docx` local mayor que `MaxFileBytes` devuelve `INVALID_FILE_SIZE`.
3. `WRITE_PDF_COPY` nombra el PDF como `wordFilename` con extensión `.pdf` y sobrescribe el existente. Si otro programa lo tiene bloqueado, escribe `<nombre> (2).pdf` y responde `renamed: true`.
4. `CONVERT_TO_PDF` tiene cupo propio: no bloquea los demás comandos, pero solo corre una conversión a la vez y una segunda recibe `SESSION_BUSY` (cierre `4002`), sin encolarse. Word corre oculto, sin alertas ni macros, y abre el documento en solo lectura. ACD solo actúa sobre la instancia de Word que creó para la conversión, nunca sobre las que ya estaban abiertas. Si el usuario abre un documento durante la conversión y Windows lo envía a esa instancia, ACD la deja abierta y visible en lugar de cerrarla. Al vencer el plazo, o si Word deja de responder, ACD termina la instancia que creó aunque contenga ese documento. El plazo es `Acd:DocumentWorkspace:ConversionTimeoutSeconds` (90 por defecto).
5. Los demás comandos del espacio de trabajo comparten un cupo: uno nuevo mientras otro está activo recibe `SESSION_BUSY` (cierre `4002`).
6. Un contenido binario que no coincide con el `size` o el `sha256` declarados, o un frame mayor que lo declarado, devuelve `WORKSPACE_INTEGRITY`.

#### Restaurar y descartar el Word (`document.workspace.restore.close`, `document.workspace.discard`)

Descartan los cambios locales del Word del remito, con o sin volver a la última versión guardada en el MPD. Ambos exigen la confirmación del usuario: `expectedSha256` es el hash del Word que el usuario vio al confirmar y `closeWithoutSaving` debe ser `true`.

| Dirección | Mensaje | Campos |
|-----------|---------|--------|
| Web → ACD | `RESTORE_WORD` + 1 frame binario | `requestId` (UUID), `anio`, `numeroEmision`, `filename`, `size`, `sha256`, `expectedSha256`, `closeWithoutSaving` |
| ACD → Web | `WORD_RESTORED` | `requestId`, `filename`, `changedAt`, `opened` |
| Web → ACD | `DISCARD_WORD` | `requestId`, `anio`, `numeroEmision`, `filename`, `expectedSha256`, `closeWithoutSaving` |
| ACD → Web | `WORD_DISCARDED` | `requestId`, `filename` |

1. ACD comprueba que `filename` siga siendo el Word del remito y que su contenido en disco tenga el hash `expectedSha256`. Si no, responde `WORKSPACE_FILE_CHANGED` sin tocar nada.
2. Cierra en Word solo ese documento, sin guardar; los demás documentos abiertos, con o sin cambios, no se tocan.
3. `RESTORE_WORD` reemplaza el archivo de forma atómica con el contenido recibido y lo vuelve a abrir con la aplicación predeterminada; `opened` informa el resultado. `DISCARD_WORD` borra el archivo y no abre otro.
4. Si Word no puede cerrar el documento (por ejemplo, con un diálogo abierto) o el archivo sigue bloqueado, responde `FILE_LOCKED`. Si ACD ya había cerrado el documento, lo vuelve a abrir desde el disco; los cambios sin guardar de esa sesión de Word se pierden.
5. Ambos comparten el cupo de los comandos del espacio de trabajo.

#### Vigilancia de la carpeta del remito (`document.workspace.watch`)

La sesión de vigilancia queda abierta y envía el Word del remito cada vez que cambia: `AUTH` → `AUTH_OK`, `WATCH_WORKSPACE`, luego `WORKSPACE_WATCHING` y cualquier cantidad de `WORKSPACE_CHANGED` hasta que la web envía `STOP_WATCH`, que ACD responde con cierre `1000`.

| Dirección | Mensaje | Campos |
|-----------|---------|--------|
| Web → ACD | `WATCH_WORKSPACE` | `requestId` (UUID), `anio`, `numeroEmision` |
| ACD → Web | `WORKSPACE_WATCHING` | `requestId`, `folder` (ruta absoluta de la carpeta del remito), `latestWord` o `null` |
| ACD → Web (push) | `WORKSPACE_CHANGED` | `requestId`, `latestWord` o `null` |
| Web → ACD | `STOP_WATCH` | `requestId`; ACD cierra con `1000` |

- `latestWord` es `{ filename, changedAt, size, sha256 }`: el Word del remito, con `changedAt` como fecha efectiva en hora local y `sha256` en hexadecimal minúscula. `null` indica que la carpeta no tiene Word.
- `WATCH_WORKSPACE` crea la carpeta si falta. `WORKSPACE_WATCHING` trae el estado inicial y cada push lleva el mismo `requestId`.
- Los cambios se agrupan (`Acd:DocumentWorkspace:WatchDebounceMilliseconds`, 1000 por defecto) y esperan a que el archivo quede estable, así que un guardado de Word mediante archivo temporal y renombrado produce un solo push. Un estado igual al último enviado no se repite.
- Durante la vigilancia ACD solo acepta `STOP_WATCH` y el cierre del WebSocket. Un `STOP_WATCH` con otro `requestId` devuelve `INVALID_REQUEST_ID`; cualquier otro mensaje, `UNEXPECTED_MESSAGE`.
- Las sesiones de vigilancia no ocupan cupo: varias pestañas pueden vigilar a la vez, dentro del límite global de 5 conexiones, sin bloquear los comandos del espacio de trabajo. Tampoco cuentan como trabajo activo para la actualización automática, así que una actualización puede cerrarlas; la web reconecta y el nuevo `WORKSPACE_WATCHING` corrige su estado.

#### Códigos de error

Todos son terminales.

| Código | Categoría | Cuándo |
|--------|-----------|--------|
| `INVALID_REQUEST_ID` | `SYSTEM` | `requestId` no es un UUID, o `STOP_WATCH` trae un `requestId` distinto del vigilado |
| `WORKSPACE_INVALID_KEY` | `USER_ACTIONABLE` | `anio` o `numeroEmision` con formato inválido |
| `WORKSPACE_INVALID_FILENAME` | `USER_ACTIONABLE` | `filename` o `wordFilename` no es un nombre `.docx` seguro |
| `INVALID_FILE_SIZE` | `SYSTEM` | `size` fuera de 1 a `MaxFileBytes`, un `.docx` local sobre el límite en `READ_WORD`, o un PDF convertido sobre el límite |
| `WORKSPACE_INTEGRITY` | `TRANSIENT` | `sha256` o `expectedSha256` no tiene 64 caracteres hexadecimales, el contenido binario no coincide con lo declarado, o falta `closeWithoutSaving: true` en `RESTORE_WORD` o `DISCARD_WORD` |
| `WORKSPACE_FILE_EXISTS` | `USER_ACTIONABLE` | `WRITE_WORD`: ya existe un archivo con ese nombre |
| `WORKSPACE_FILE_NOT_FOUND` | `USER_ACTIONABLE` | `READ_WORD`, `RESTORE_WORD` o `DISCARD_WORD`: el archivo no existe |
| `WORKSPACE_FILE_CHANGED` | `USER_ACTIONABLE` | `RESTORE_WORD` o `DISCARD_WORD`: el Word cambió después de la confirmación |
| `FILE_LOCKED` | `USER_ACTIONABLE` | `RESTORE_WORD` o `DISCARD_WORD`: Word no cerró el documento o el archivo sigue bloqueado |
| `WORKSPACE_FOLDER_NOT_FOUND` | `USER_ACTIONABLE` | `OPEN_FOLDER`: la carpeta del remito no existe |
| `WORKSPACE_IO_FAILED` | `TRANSIENT` | No se pudo leer o escribir la carpeta o un archivo |
| `PROCESS_START_FAILED` | `USER_ACTIONABLE` | `OPEN_FOLDER`: Windows no pudo iniciar el Explorador |
| `WORD_NOT_INSTALLED` | `USER_ACTIONABLE` | `CONVERT_TO_PDF`: Microsoft Word no está instalado |
| `CONVERSION_TIMEOUT` | `TRANSIENT` | Word no terminó la conversión dentro de `ConversionTimeoutSeconds` |
| `CONVERSION_FAILED` | `SYSTEM` | Word falló o no produjo un PDF válido |
| `WORKSPACE_WATCH_FAILED` | `SYSTEM` | `WATCH_WORKSPACE`: no se pudo crear o vigilar la carpeta (cierre `1011`) |
| `SESSION_BUSY` | `TRANSIENT` | Hay otro comando o conversión activo (cierre `4002`) |

## Stack

| Componente | Detalle |
|------------|---------|
| Runtime | .NET 10 (`net10.0-windows`), `win-x64` autocontenido |
| Servidor | ASP.NET Core Kestrel (WebSocket y health check HTTP) |
| UI | Icono de bandeja con WinForms |
| Logs | Serilog (consola y archivo rotativo) |
| Instalador y actualizaciones | [Velopack](https://velopack.io) 1.2.0 |
| CI/CD | GitHub Actions |

## Canales de distribución

Dos variantes conviven en el mismo equipo, con instalación, esquema URI y mutex propios:

| Variante | Pack ID | Canal | Esquema URI | Tipo de release |
|----------|---------|-------|-------------|-----------------|
| Producción | `ACD` | `stable` | `acd://` | Estable |
| Desarrollo | `ACD-Dev` | `dev` | `acd-dev://` | Pre-release |

La variante se fija al compilar con `AssemblyMetadata` (`-p:AcdVariant=Prod|Dev`), de modo que el binario instalado conoce su identidad sin depender de variables de entorno.

## Compilar y ejecutar

**Requisitos:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) y [Velopack CLI](https://docs.velopack.io/) (`dotnet tool install -g vpk`).

```powershell
dotnet build src/ACD/ACD.csproj

# Variante de desarrollo (canal dev, esquema acd-dev://)
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/ACD/ACD.csproj
```

Pruebas:

```powershell
dotnet test
# Prueba de humo con Microsoft Word real: restaurar y descartar
dotnet run --project tests/ACD.WordRestore.Smoke
```

## Publicación

Los tags de git disparan la publicación en GitHub Releases mediante Velopack.

| Patrón de tag | Ejemplo | Workflow | Resultado |
|---------------|---------|----------|-----------|
| `vX.Y.Z` | `v1.2.3` | `release-prod.yml` | Release estable, canal `stable` |
| `vX.Y.Z-dev.N` | `v1.2.3-dev.1` | `release-dev.yml` | Pre-release, canal `dev` |

Cada workflow corre en `windows-latest` con el flujo recomendado por Velopack:

```
git tag → GitHub Actions
  ├─ dotnet publish (autocontenido, win-x64)
  ├─ vpk download (feed del release anterior para generar deltas)
  ├─ vpk pack (instalador y paquetes delta)
  └─ vpk upload (publica en GitHub Releases)
```

El instalador incluye el runtime de .NET 10; el usuario no instala nada más.

## Actualización automática

Un servicio en segundo plano busca actualizaciones cada 6 horas; la primera revisión ocurre 60 segundos después del arranque. Las descarga en silencio y solo las aplica por acción explícita del usuario, nunca durante una firma en curso.

## Estructura

```
src/ACD/
  Configuration/      Opciones de la aplicación y de actualización
  DocumentWorkspace/  Espacio de trabajo del remito: rutas, Word, copia PDF, conversión, restauración y descarte
  Files/              Utilidades de archivos (sondeo de estabilidad, apertura con el shell)
  Firma/              Depósito de archivos y vigilancia de FirmaONPE
  System/             Icono de bandeja, instancia única, registro del esquema URI
  Update/             Servicio de actualización
  WebSocket/          Middleware, manejador de sesión y mensajes del protocolo
  Program.cs
tests/
  ACD.Tests/              Pruebas unitarias
  ACD.WordRestore.Smoke/  Prueba de humo con Microsoft Word
.github/workflows/
  release-dev.yml
  release-prod.yml
```

## Licencia

[MIT](LICENSE)
