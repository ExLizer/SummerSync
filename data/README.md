# data

Datos versionados que el mod lee en runtime, para poder ajustarlos sin recompilar.
Se llena a partir de los primeros dumps reales de la Fase 1.

| Archivo | Contenido | Fase |
|---|---|---|
| `dump-noise.txt` | Lo que el juego cambia solo (relojes, timers). Una expresión regular por línea; lo genera `dumpdiff noise`. | 1 |
| `fsm-classification.draft.md` | Borrador automático de la clasificación, una fila por nombre de FSM. Lo genera `dumpdiff classify --review`. Para revisar, no para usar. | 1 |
| `fsm-classification.draft.json` | El mismo borrador por instancia (6.667 entradas). No se versiona: se regenera con `dumpdiff classify`. | 1 |
| `fsm-classification.json` | Clasificación revisada de cada FSM de PlayMaker: `WorldLogic`, `Presentation`, `Mixed` o `Ignore`. | 6 |
| `vehicles/*.json` | Por vehículo: un nombre corto para los comandos, el objeto raíz, el trigger que ofrece manejar (nada para lo que solo se remolca, como el acoplado), la variable de su llave, la caja del interior (donde se viaja de pasajero y va la carga), dónde están la perilla de luces y la electricidad, las perillas (`knobs`, también las leídas por estado y las que dice solo quien simula), la bocina (`horn`), los enganches (`hitches`) y las palancas hidráulicas (`hydraulics`). Viajan dentro de `SummerSync.dll`; los lee `VehicleDefinition`, formato 2. | 5 |

Nada de acá puede escribirse a mano por suposición: las rutas de GameObjects y los nombres de FSM salen del dumper
y se anotan en `docs/GAME_NOTES.md`.
