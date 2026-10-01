# Classic Forever Launcher

Launcher para jugar en el servidor **Classic Forever** con el cliente beta **1.60.1** (`WowB.exe`).
Sustituye a `play-beta.bat` / `Jugar-Beta.bat`: hace lo mismo, pero con una ventana, el estado del servidor
las novedades y las notas del parche. En **español e inglés** (selector ES | EN arriba a la derecha; la primera vez usa el idioma de Windows).

El diseño de la ventana (marco, logo y botones, desde la 1.2.0) es de **Zemog**, jugador de la comunidad.

![captura](docs/screenshot.png)

## Descarga

**[Última versión → Releases](https://github.com/defexnicolas/wow-classic-launcher/releases/latest)** — descarga
`ClassicForever.exe` y ponlo donde quieras (si lo pones dentro de la carpeta `_classic_beta_`, la encuentra solo).

Requisitos: Windows 10 u 11 (trae .NET Framework 4.8 de serie), el cliente beta instalado desde Battle.net
(build `1.60.1.69913` o `1.60.1.69977`) y una cuenta en el servidor. No pide permisos de administrador.

### "Windows protegió su PC"

El ejecutable **no está firmado** (un certificado de firma cuesta cientos de dólares al año), así que SmartScreen
avisa la primera vez: pulsa **Más información → Ejecutar de todas formas**.

Para que no tengas que fiarte de nosotros:

- El `.exe` de cada release lo compila **GitHub Actions** a partir del código de este repo; el enlace al registro
  del build y el **SHA-256** están en las notas de la release. Compruébalo con
  `Get-FileHash ClassicForever.exe` en PowerShell.
- Puedes compilarlo tú: `build.cmd` usa el compilador de C# que ya trae Windows. No hace falta instalar nada.
- Es .NET sin ofuscar: cualquier descompilador (ILSpy, dnSpy) muestra el mismo código que hay aquí.

## Linux

**Lo más fácil: la AppImage.** Descarga `ClassicForever-x86_64.AppImage` de
[Releases](https://github.com/defexnicolas/wow-classic-launcher/releases/latest), dale permiso de ejecución y ábrela:

```bash
chmod +x ClassicForever-x86_64.AppImage && ./ClassicForever-x86_64.AppImage
```

Es la misma ventana que en Windows (estado, noticias, notas del parche, opciones) y **no necesita Steam ni `sudo`**:
abre `WowB.exe` con Proton mediante [umu-launcher](https://github.com/Open-Wine-Components/umu-launcher) (va dentro) y
pone la clave del servidor. **Si ya tienes Proton** (el de Steam, GE-Proton, proton-cachyos, el de Lutris o Heroic) lo
usa: la primera vez solo descarga el entorno de Steam (≈300 MB, en `~/.local/share/umu`). Si no tienes ninguno, descarga
también GE-Proton (≈1,5 GB en total). En **Opciones** puedes elegir qué Proton usar, cambiar a Wine del sistema y añadir
el launcher al menú de aplicaciones.

- Sigue haciendo falta el cliente beta instalado (con Battle.net en Lutris/Bottles/Steam, o copiando la carpeta
  `_classic_beta_` desde un Windows). La AppImage busca `_classic_beta_` sola en los prefijos habituales y en los discos
  de Windows montados; si no la encuentra, elígela en Opciones.
- Usa un prefijo de Wine propio (`~/.local/share/classic-forever-launcher/prefix`): no toca el de Battle.net.
- Registro en `_classic_beta_/Logs/launcher-linux.log` (y lo que dice Proton en `proton.log`).
- Se compila en GitHub Actions con [`linux/appimage/build.sh`](linux/appimage/build.sh) (Python 3.12 portable con Tk +
  umu-launcher, comprobados por SHA-256).

### A mano: Steam, Wine o Lutris

Si prefieres tu propia configuración, [`linux/classic-forever-linux.py`](linux/classic-forever-linux.py)
(Python 3, sin dependencias) hace lo mismo desde fuera del juego. También está dentro de la AppImage:
`./ClassicForever-x86_64.AppImage --patcher <comando del juego>` acepta los mismos argumentos.

**Steam + Proton**
1. Descarga el script: `curl -LO https://raw.githubusercontent.com/defexnicolas/wow-classic-launcher/main/linux/classic-forever-linux.py`
2. En Steam: *Añadir un juego → Añadir un juego ajeno a Steam* → elige **`WowB.exe`** (no Battle.net).
3. Propiedades del juego → *Compatibilidad*: fuerza Proton. *General → Opciones de lanzamiento*:
   ```
   python3 /ruta/completa/classic-forever-linux.py %command%
   ```
4. Juega desde Steam. El script escribe el portal, abre el juego y pone la clave; su registro queda en
   `_classic_beta_/Logs/launcher-linux.log`.

**Proton sin Steam (umu-run)**: `python3 classic-forever-linux.py umu-run "/ruta/_classic_beta_/WowB.exe"`.

**Wine a mano**: `python3 classic-forever-linux.py wine "/ruta/_classic_beta_/WowB.exe"`.

**Todo en uno**: [`linux/classic-forever.sh`](linux/classic-forever.sh) descarga el parcheador y, si el juego ya esta
abierto, se engancha (pide `sudo` solo para eso); si no, lo abre con `wine`. Edita `GAME_DIR` o pasalo por entorno.
`--update` vuelve a descargar el parcheador, `--launch` fuerza abrir el juego.

Como el script abre el juego, Linux le deja acceder a su memoria sin `sudo`. Para engancharse a un juego ya abierto:
`sudo python3 classic-forever-linux.py --pid <PID> --game-dir "/ruta/_classic_beta_"`.

Avisos (medidos el 27-09-2026 en CachyOS):
- **Nunca abras el juego con `sudo`**: Wine correria como root sobre tu prefijo, deja ficheros de root en `~/.wine` y en
  `_classic_beta_/Cache` y el cliente falla despues con `BC_ASSERT`. Si ya paso: `sudo chown -R $USER:$USER ~/.wine
  "/ruta/_classic_beta_"` y borra `_classic_beta_/Cache`.
- Si el juego vuelve al login nada mas conectar ("has sido desconectado"), la clave no estaba puesta: mira el registro.
  Bajo Wine/Proton el almacen de claves lleva relleno `00` en vez de `7f`; las versiones del parcheador anteriores al
  27-09 lo descartaban y nunca parcheaban. Vuelve a descargar el script.

## Qué hace (y qué no)

Al pulsar **JUGAR**:

1. Crea o actualiza `WTF\BetaSuspendedTest.wtf` con `SET portal "auth.gpon.com.co"` (copia tu `Config.wtf` la
   primera vez, para conservar tus gráficos y tu sonido). **Tu `Config.wtf` no se toca**: abrir el juego desde
   Battle.net sigue yendo al beta oficial.
2. Abre `WowB.exe -config BetaSuspendedTest.wtf`.
3. Espera a que el cliente prepare la conexión y **sustituye en su memoria una clave pública** (32 bytes, la del
   grupo de región 8) por la clave pública del servidor, para que el cifrado del mundo funcione. Solo escribe si el
   almacén de claves completo coincide con el que se midió, y solo en memoria de datos (heap), nunca en código ni
   en disco. El código está en [`src/Patcher.cs`](src/Patcher.cs).
4. Se queda en la bandeja del sistema mientras juegas (reaplica la clave si el cliente la reinicia) y se cierra
   solo cuando cierras el juego.

**No** modifica ficheros del juego (salvo esa línea del `.wtf`, y la carpeta `Cache` si pulsas **Opciones → Borrar caché**,
que solo funciona con el juego cerrado y pide confirmación), **no** descarga ni ejecuta nada, **no** toca otros
procesos y **no** envía datos tuyos a ningún sitio. El registro queda en `_classic_beta_\Logs\launcher.log`.

> Casi siempre entras a la primera. Si el primer intento de entrar al reino falla ("reason 24" o vuelves al
> login), espera al aviso **Listo** y vuelve a entrar **sin cerrar el juego**.

## Addons del servidor

El launcher (Windows y la AppImage) instala y mantiene al día los addons propios del servidor, por ahora
[`ClassicForever_Bots`](addons/ClassicForever_Bots) (panel de los bots de mazmorra: llenar el grupo, darles órdenes y
ver su vida y maná). Se desactiva en **Opciones → Addons del servidor**.

- Los anuncia `status.json` (`"addons": [{name, version, url, sha256}]`) y solo se descargan de las Releases de este
  repo; el zip se comprueba con su SHA-256 (el del feed o el `.sha256` publicado al lado).
- Solo se aceptan ficheros de addon (`.lua .toc .xml .png .tga .blp .md .txt`) dentro de la carpeta del addon; si algo
  no cuadra no se toca la versión instalada.
- Nunca con el juego abierto: se reintenta al cerrarlo. Se instala en `_classic_beta_\Interface\AddOns\<nombre>`.
- El workflow empaqueta cada carpeta de `addons/` en `<nombre>.zip` + `.sha256` en cada Release.

## Campo de visión (FoV)

El cliente limita el CVar `cameraFov` a 50..90 grados: cualquier otro valor lo devuelve a 90. En **Opciones → Campo de
visión** se marca **Desbloquear** y se elige un valor con el deslizador (60..150). Se aplica al momento mientras el
juego está abierto y se quita igual al desmarcar; se guarda en `%APPDATA%\ClassicForeverLauncher\fov.txt`.

Cómo funciona ([`src/Fov.cs`](src/Fov.cs)): **nunca se modifica el código del cliente** (su anti-tamper comprueba el
código y cierra el juego con un "Security Crash" si cambia). El launcher solo lee el código para localizar, por firma
de bytes y sin depender de la build, el validador del CVar y la cámara, y escribe dos datos en el heap: el valor del
CVar (float e int) y el fov en radianes del objeto cámara. Lo revisa cada segundo y lo reaplica tras una pantalla de
carga. Con la opción activa escribe `SET cameraFov "90"` en `BetaSuspendedTest.wtf` para que el validador se ejecute
al arrancar (su página de código viene cifrada y solo se descifra al ejecutarse).

### Comandos en el juego

Se escriben en el chat. `/console` y `/run` son comandos normales del cliente.

| Qué | Comando | Nota |
|---|---|---|
| Ver el valor del CVar | `/run print(GetCVar("cameraFov"))` | Devuelve el **texto** del CVar. El launcher escribe el float y el int, no el texto, así que seguirá diciendo `90` aunque la cámara esté a 110. |
| Ver el valor por defecto | `/run print(GetCVarDefault("cameraFov"))` | Siempre `90`. |
| Poner un valor (sin launcher) | `/console cameraFov 80` | Solo acepta 50..90; fuera de ese rango vuelve a 90. |
| Volver al valor normal | `/console cameraFov 90` | Si el launcher tiene la opción activa, la reaplica al segundo siguiente: desmárcala antes en Opciones. |
| Suavizado del cambio de FoV | `/console cameraFoVSmoothSpeed 10` | CVar del cliente sin relación con el límite. |

La forma fiable de saber qué FoV está usando la cámara es la propia imagen y la barra de estado del launcher
("Campo de visión: 110°"), o la línea `fov:` en `_classic_beta_\Logs\launcher.log`: solo aparecen cuando el valor se
ha verificado en memoria. El cliente no expone ninguna función Lua que lea el fov real de la cámara.

La cámara exige 0 < fov < 180; el deslizador se queda en 60..150 porque por encima la imagen se deforma mucho en los
bordes. En Linux ([`classic-forever-linux.py`](linux/classic-forever-linux.py)) esta opción todavía no existe.

## Estado del servidor

La ventana muestra si el servidor está en línea de dos formas:

- **Sonda directa**: abre y cierra una conexión TCP con el login (`1119`) y el mundo (`8085`). Así sabe en tiempo
  real si está arriba, abajo o si solo responde el login.
- **`status.json`** en la rama [`status`](../../tree/status) de este repo: mantenimiento,
  novedades, enlaces y la última versión del launcher. Lo publica el servidor cada minuto con
  [`server/publish_status.py`](server/publish_status.py). Si tiene más de 15 minutos, el launcher no se fía de él.

Si hay una versión nueva, el launcher **solo avisa** y abre esta página: no se actualiza solo. Solo si el servidor deja
de admitir tu versión (`launcher.min`), el botón JUGAR pasa a **ACTUALIZAR**.

## Problemas

| Síntoma | Qué hacer |
|---|---|
| "No encuentro WowB.exe" | **Cambiar carpeta** y elige `_classic_beta_` (o la carpeta `World of Warcraft` que la contiene). |
| Datos raros o vacíos en objetos, misiones o NPC | **Opciones → Borrar caché** con el juego cerrado. |
| "Tu cliente es la build …" | Battle.net actualizó el cliente a un build que el servidor todavía no admite. Espera a la próxima versión. |
| "No llego desde tu red" | El servidor está en línea pero tu red no llega a `auth.gpon.com.co` (firewall, VPN, DNS). |
| Error 1023 al conectar | El portal debe ser `auth.gpon.com.co`; el launcher lo corrige solo al pulsar JUGAR. |
| El antivirus lo borra | Es un falso positivo (el launcher escribe en la memoria de otro proceso). Añade una excepción o compílalo tú. |
| Se cierra con un error | Manda `%LOCALAPPDATA%\ClassicForeverLauncher\crash.txt` y `Logs\launcher.log`. |

## Compilar

```bat
build.cmd            :: Windows: deja dist\ClassicForever.exe
```
```bash
./build.sh           # desde WSL (copia a %TEMP% y llama a build.cmd)
```

Publicar una versión: sube `App.Version` en `src/App.cs` y `launcher.version` en `server/news.json`, y empuja un
tag `vX.Y.Z`. El workflow compila, calcula el SHA-256 y crea la release.

## Para el administrador del servidor

- `server/news.json`: textos en español; añade `title_en`, `text_en`, `message_en` o `label_en` para la versión en inglés (si falta, se muestra el español). Mantenimiento (`"maintenance": true` + `"message"`), novedades, enlaces (solo `https://`) y la
  versión publicada del launcher. Los cambios salen en el siguiente minuto.
- `patchNotes`: lista con el mismo formato que `news` (`date`, `title`, `text`, `url`, `*_en`); sale en la pestaña
  **Notas del parche**.
- `addons`: `[{"name", "version", "url"[, "sha256"]}]`; `url` tiene que ser una Release de este repo. La versión se
  compara con `## Version:` del `.toc` instalado.
- `launcher.min`: versión mínima admitida. Los launchers por debajo cambian JUGAR por ACTUALIZAR (lo entienden desde la
  1.2.0; los anteriores solo ven el aviso de `launcher.version`). Úsalo solo cuando una versión vieja ya no sirva.
- `server/publish_status.py --once` imprime el JSON sin publicar; `server/classic-forever-status.service` lo deja
  como servicio de usuario de systemd.

Licencia MIT. Proyecto de fans, sin relación con Blizzard Entertainment.
