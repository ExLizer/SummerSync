# tools/satsuma

`make-parts.py` genera `data/satsuma/parts.json`, el catálogo de las piezas del Satsuma que el mod lleva embebido
(`SummerSync.satsuma.parts.json`) y que lee `SummerSync.Core.Vehicles.SatsumaCatalog`. Lee dos cosas, sin
modificarlas: un dump completo del juego (`SummerSyncDumper`, Ctrl+F10) y el archivo de nivel `level2` del juego (con
UnityPy). No contiene datos del juego: solo saca de esas dos fuentes nombres, rutas y números.

```
python -I tools\satsuma\make-parts.py "<dump completo>.json" "<juego>\mysummercar_Data\level2" ^
    -o data\satsuma\parts.json --notes "<notas>.txt" --pydeps "<carpeta con UnityPy>"
```

El catálogo actual sale de `dump-20261014-222924-full.json` (partida nueva, Satsuma desarmado en el garaje) y del
`level2` del build `5.0.0f4/e30ef95a`. Las notas (`--notes`) listan primero las entradas a confirmar y después todo lo
que el generador dejó afuera o dedujo, con el motivo.

## Qué es una entrada

Una por cada FSM `Data` bajo `Database/Database{Motor,Body,Mechanics,Orders,Wiring,Computer}/<Nombre>` y
`Database/PartsStatus/<Nombre>` (239 en ese dump), más cuatro que el juego no tiene: `Wheels/FL`, `FR`, `RL`, `RR`, las
ruedas, que no tienen entrada en la base y se instalan en los triggers `TriggerWheel<XX>_New`. El archivo está ordenado
por nombre (orden ordinal); el índice de una entrada es su posición, y así la nombra la sesión.

## Rutas

Toda ruta del catálogo es una de estas:

| Forma | Significa |
|---|---|
| `Body/trigger_door_left` | debajo de la raíz del auto, `SATSUMA(557kg, 248)` |
| `/ITEMS/motor hoist(itemx)/Hoist` | desde la raíz de la escena |
| `DatabaseMotor/Block:trigger_piston1` | debajo del objeto de la pieza de esa entrada, que se mueve (al instalarse el bloque, el trigger del pistón ya no está en `CARPARTS`). En el juego, ese objeto es el que tiene la variable `partVariable` de la FSM `Data` de la entrada |
| `DatabaseBody/Door_Left:` | el objeto de la pieza mismo |
| `wheel:trigger_hubcap` | debajo de una rueda (objeto con FSM `Use` que tiene `TireType`); `Wheels/FL:Bolts/BoltPM` es la rueda puesta en esa esquina |

El host de una ruta es la entrada más profunda cuya `ThisPart` (en el dump) contiene esa ruta. Las piezas de la familia A
no son host: sus copias `(xxxxx)` no se mueven respecto de quien las contiene.

## Campos de una entrada, y de dónde sale cada uno

| Campo | Qué es | Fuente |
|---|---|---|
| `name` | ruta debajo de `Database/` | ruta de la FSM `Data` |
| `group` | `Body`, `Motor`, `Mechanics`, `Orders`, `Wiring`, `Computer`, `PartsStatus` o `Wheels` | la carpeta de la base |
| `family` | `A` (el clon se destruye y se prende la copia `(xxxxx)` del auto), `B1` (se reparenta sin cuerpo), `B2` (se reparenta con cuerpo y joint), `wheel`, `wiring`, `status` (ítem de tienda con estado propio), `hoist` (el bloque), `other` | `wiring`/`status`/`hoist`/`wheel` por grupo o nombre; las demás, de los triggers (abajo) |
| `data` | qué banderas tiene la FSM `Data`: `Installed`, `Bolted`, `Damaged`, `Purchased`, `InHoist` | variables bool de `Data` |
| `part` | nombre del objeto de la pieza suelta como lo llama el juego (`door left(Clone)`) | último segmento de `Data.ThisPart` o `Data.SpawnThis`; en los ítems, el `HandChild` del trigger |
| `partVariable` | la variable de `Data` que apunta al objeto de la pieza: `ThisPart` (B), `SpawnThis` (A: el clon suelto original) o `Current…` (ítems; vacía en un auto desarmado) | variables `GameObject` de `Data` |
| `copies` | familia A: las copias `(xxxxx)` que el trigger prende | `Data.ActivateThis`, `ActivateThis1`, `ActivateThis2` |
| `spawn` | familia A: el nombre del clon nuevo que crea `Removal` al sacarla | `Removal.SpawnPart` + `(Clone)` (confirmado con el subchasis: puente, 2026-10-15) |
| `triggers` | las ranuras donde entra (ver abajo) | FSM `Assembly` |
| `removals` | objetos con la FSM `Removal` que la saca con `Detach = true` | FSM `Removal` cuyo `db_ThisPart` es la entrada; si no tiene, la que está en una de sus `copies` (las fundas); en los ítems, la del ítem mismo (el trigger hace `EnableFSM Removal` sobre `$Part`) |
| `blockedBy` | entradas cuya variable en `true` impide que el jugador la saque (la ruta de `Detach` no lo mira) | `Removal.db_RemoveReq<n>` leídos en el estado `Requirements` con `GetFsmBool`; si el estado no está cargado, de las cadenas del nivel (k-ésimo objeto con k-ésimo par `"Data"`, variable) |
| `boltChecks` | las FSM que suman los tornillos (ver abajo) | FSM `BoltCheck` cuyo `db_ThisPart` es la entrada; en las ruedas, la FSM `Use` de la rueda |
| `wiring` | `ends`: los dos conectores (FSM `Assemble`); `state`: `Finish assembly`; `activates`: lo que prende | FSM `Assemble` con `db_ThisPart` de la entrada; `ThisWire1`/`ThisWire2` |
| `installedBy` | cables que también la marcan instalada (los bornes de la batería) | `SetFsmBool Installed` de otra entrada en `Finish assembly` (`db_ThisPart2`) |

### Un trigger

Cómo pasa el juego una pieza a instalada sin mouse (lo mismo que hace al cargar la partida): poner la variable `Part`
del trigger en la pieza y entrar al estado `state` con una transición global (o mandar `ASSEMBLE` si `event`).

| Campo | Qué es | Cómo se deduce |
|---|---|---|
| `path` | objeto con la FSM `Assembly` | ruta del dump |
| `part` | nombre de la pieza que acepta esta variante | `StringCompare.compareTo` del evento que lleva al estado de clic, o `GetChild.childName` sobre `ItemPivot`, o `HandChild`; si nada de eso está cargado, el `part` de la entrada (nota) |
| `state` | el estado donde entrar | el clic: el estado con `GetMouseButtonDown` y el destino de su evento. Desde ahí se busca el estado que hace `SetFsmBool Installed = true` sobre la entrada (o el que hace `SetFsmBool Use.Installed` sobre `$Part` en los ítems, o el primer `Assemble…` en las ruedas). `state` es el primero del camino desde el que la instalación sigue sin decisiones: se salta `Check bolts` y demás comprobaciones, que en otra PC podrían soltar otra pieza |
| `event` | si el estado de reposo (`Check for Part collision…`, al que lleva `Check Assembly`) tiene una transición `ASSEMBLE` que llega a la misma instalación | transiciones del dump (puerta sí, subchasis no: puente, 2026-10-15) |
| `setup` | la bandera de la carga (`Setup`) que prueba `Check Assembly` o el reposo con `BoolTest … isTrue: ASSEMBLE` | acciones del dump |
| `sound` | si desde `state` suena `CarBuilding` (`assemble`); en los pistones, por ejemplo, el sonido queda en `Wait`, antes de `Check bolts` | `MasterAudioPlaySound` en los estados del camino |
| `family` | lo que hace la variante con `$Part`: `DestroyObject` → A; `AddComponent HingeJoint/FixedJoint` → B2; `DestroyComponent Rigidbody` o `SetParent` → B1; estado `Set data` → wheel | acciones del camino |
| `requires` | `{entry, variable, value}`: lo que el trigger lee de la base antes de aceptar la pieza | `GetFsmBool` sobre `Data` en cualquier estado + el `BoolTest`/`BoolAllTrue`/`BoolNoneTrue` cuyo evento lleva al camino de la instalación (no al reposo) |
| `unloaded` | el dump no tenía cargadas las acciones de esa FSM: lo anterior sale de la forma del grafo, de los tipos de acción y de las cadenas del nivel por estado | — |
| `shared` | un mismo trigger sirve a varias entradas, que se distinguen por la pieza (las tazas: el trigger lee la entrada del `Removal.db_ThisPart` de la pieza) | — |

Las variantes (ruedas normal/offset, parrilla y parrilla GT, capó y capó de fibra, tapas de válvulas, volantes,
alerones) son un trigger por entrada con su propio `state`.

### Un control de tornillos y sus tornillos

| Campo | Qué es | Fuente |
|---|---|---|
| `path` | objeto con la FSM (`BoltCheck`, o `Use` en las ruedas: `fsm`) | dump |
| `yes`, `no` | `BoltedYES`, `BoltedNO`: con `Tightness` ≥ `yes` la entrada queda `Bolted` | variables del dump |
| `bolts[].index` | el índice del tornillo en la lista de su pieza, como lo calcula el juego | el primer hijo `Untagged` del `BoltPM` (en el nivel) se llama `bolt<n>`; la FSM `Screw` (`Save data 2`) hace `GetChild` (tag `Untagged`), `GetName`, `GetStringRight` con `charCount` = **1** (leído del binario de la acción en `level2`) y `ConvertStringToInt`: el índice es el último dígito. `bolt10` da 0 |
| `bolts[].path` | el `BoltPM` (todos los de una pieza se llaman igual; se distinguen por `index`) | dump; el `BoltPM` del nivel se busca por ruta y por los índices de hermanos desde abajo |
| `bolts[].size` | la medida de llave | `2Spanner/Raycast :: Check`, estado `Check tool`: `GetScale` del `BoltPM` en espacio `Self` (enum 1, leído del binario), `xScale` → `BoltSize`, comparado con `ToolWrenchSize` con tolerancia 0,02. Se guarda la escala local x del nivel |
| `bolts[].stages` | la etapa más alta (8: nueve `TIGHTEN` hasta `8 2`; confirmado en el puente) | los estados `<n> 2` de `Screw` |

Un tornillo pertenece a una entrada por su `Screw.PartAssembled`, que apunta al objeto con su `BoltCheck`: algunos
están fuera de la pieza (los del palier, los del amortiguador en el husillo, los de los soportes del motor, dos del
forro de freno en los husillos).

## Lo que no sale de la base

- **Las herramientas cuyos tornillos importan al auto** (`TOOLS` en el generador): el gancho del aparejo,
  `Tools/MotorHoistHook`, grupo `Tools`, familia `other`, sin registro en la base y sin nada que instalar: solo el control
  de tornillos `/ITEMS/motor hoist(itemx)/Hoist` y sus dos tornillos. Con los dos apretados el juego cuelga el bloque del
  gancho; el mod comparte esos tornillos como los de una pieza.
- **Una pieza de estado cuyo registro no la nombra** (la batería: ni `ThisPart`, ni `SpawnThis`, ni `Current…`): su sacada
  es donde la cuelga su trigger (`SetParent` de la parte en el estado del clic), `MiscParts/pivot_battery/battery(Clone)`.

## Lo que el generador no puede saber

Lo lista en las notas. Lo principal: FSM no cargadas (objetos apagados en el dump: el volante, el tacómetro de la
columna, el arnés, el nitro, los soportes del motor, el plato de embrague sobre el volante de carrera), que hace falta
confirmar con un dump donde estén cargadas (instalando antes lo que las prende); el cable `WiringSpeakers` (su `Data` no
estaba cargada y ningún par de conectores la nombra); y el forro de freno, cuyo `BoltPM` del husillo derecho tiene el
hijo `bolt10` y comparte el índice 0 con otro.
