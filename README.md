# fd32: MagRail Systems

Magnetic levitation trains for **Space Engineers**: lay rails, build a train on hover engines
and drive it by hand or let the autopilot run it between stations.

> Work in progress: the mod is being prepared for beta. Saves are not kept compatible between
> versions until then.

[Русский ниже](#русский)

## What is in the mod

**Track**
- **Rail Block 1**: a straight rail, 1 block long. Rails placed end to end join by themselves.
- **Free Rail Block**: a rail with a turning head, set to any direction, slope and bank.
- **Rail Switch**: one entry, three branches (left, centre, right); the train goes into the selected one.
- **Embedded Rail**: a rail built into a block, flush with its top, for platforms, floors and crossings.
- **Wide** versions of the track blocks (2 m rail).

**Rail Linker** (hand tool): lays spans between track blocks, curves and slopes included,
and up to four parallel rails in one go. A span is first a free prototype, then it is built
from Rail components with the welder like a block.

**Train**
- **Hover Engine**, **Small Hover Engine**: hold the train over the rail and drive it.
  Mount them under the wagon; the platform follows the rail.
- **Train Control Unit**: inertia dampeners (Z) and cruise control for the train's cockpit.
- **Train Autopilot**: drives the train to its stations along the track. Stop the train at a place
  and press Remember: that is a station. It slows down before curves and parks connectors at stops.

Works in multiplayer and on dedicated servers.

## Install

Copy the repository folder (with `Data`, `Models`, `Textures`, `metadata.mod`) into
`%AppData%\SpaceEngineers\Mods\`, then add the mod to a world under *Mods* (local mods).

---

## Русский

Поезда на магнитной подвеске для **Space Engineers**: проложите рельсы, соберите поезд на
ховер-двигателях и ведите его сами или отдайте автопилоту, который возит его между станциями.

> Мод в разработке и готовится к бете. До беты совместимость сохранений между версиями не гарантируется.

- **Путь**: прямой рельс, свободный рельс с поворотной головкой (любое направление, уклон, крен),
  стрелка на три ветки, рельс, встроенный в блок, широкие варианты (2 м).
- **Rail Linker** (инструмент в руке): прокладывает пролёты между блоками пути (с поворотами и
  уклонами), до четырёх параллельных рельсов за раз. Пролёт сначала бесплатный прототип, потом
  его сваривают из компонента Rail, как блок.
- **Поезд**: ховер-двигатели (большой и малый), блок управления поездом (гасители инерции Z и
  круиз-контроль), автопилот (станции запоминаются кнопкой, тормозит перед поворотами, паркует
  коннекторы на остановках).

Работает в мультиплеере и на выделенном сервере.

**Установка**: скопируйте папку репозитория (`Data`, `Models`, `Textures`, `metadata.mod`) в
`%AppData%\SpaceEngineers\Mods\` и добавьте мод в мир в разделе *Моды* (локальные моды).
