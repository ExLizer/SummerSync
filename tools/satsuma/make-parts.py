# Builds data/satsuma/parts.json: the catalogue of the Satsuma's parts, slots, bolts and wiring, read from a full
# dump of the game (SummerSyncDumper) and from the game's level file (for what a dump cannot show: the name of the
# child of each bolt, its scale, and the variables an unloaded state uses). Read-only on both. tools/satsuma/README.md
# says how each field is derived.
#
# Usage:
#   python -I tools/satsuma/make-parts.py <full dump.json> <game>/mysummercar_Data/level2 -o data/satsuma/parts.json
#          [--notes <notes.txt>] [--pydeps <folder with UnityPy>]
import argparse
import collections
import json
import re
import struct
import sys

CAR = "SATSUMA(557kg, 248)"
FORMAT = 1
# Tools whose bolts the car's assembly shares, by the entry's name: the BoltCheck machine of each.
TOOLS = {"Tools/MotorHoistHook": "ITEMS/motor hoist(itemx)/Hoist"}
GROUPS = ("DatabaseMotor", "DatabaseBody", "DatabaseMechanics", "DatabaseOrders", "DatabaseWiring", "DatabaseComputer",
          "PartsStatus")
DATA_FLAGS = ("Installed", "Bolted", "Damaged", "Purchased", "InHoist")
CORNERS = ("FL", "FR", "RL", "RR")

notes = []


def note(kind, text):
    notes.append((kind, text))


# --- the dump ------------------------------------------------------------------------------------------------------


def value(param):
    """A parameter's value: the variable's value if it is bound to one."""
    if isinstance(param, dict) and "var" in param:
        return param.get("v")
    return param


def variable(param):
    return param.get("var") if isinstance(param, dict) and "var" in param else None


def basename(path):
    return path.rsplit("/", 1)[-1] if path else None


class Fsm:
    def __init__(self, raw):
        self.raw = raw
        self.path = raw["path"]
        self.ipath = raw.get("ipath", "")
        self.name = raw["name"]
        self.start = raw.get("start")
        self.vars = raw.get("vars") or {}
        self.states = collections.OrderedDict((s["name"], s) for s in raw.get("states") or [])
        self.level = None  # state name -> strings of that state in the level file, filled on demand

    def gvar(self, name):
        return (self.vars.get("gameObject") or {}).get(name)

    def fvar(self, name):
        return (self.vars.get("float") or {}).get(name)

    def bvars(self):
        return self.vars.get("bool") or {}

    def loaded(self, state):
        return bool(self.states[state].get("loaded"))

    def all_loaded(self):
        return all(s.get("loaded") for s in self.states.values())

    def actions(self, state):
        return self.states[state].get("actions") or []

    def trans(self, state):
        return [(t.get("e"), t.get("to")) for t in self.states[state].get("trans") or []]

    def target(self, state, event):
        for e, to in self.trans(state):
            if e == event:
                return to
        return None


def load_dump(path):
    with open(path, encoding="utf-8") as stream:
        dump = json.load(stream)
    if dump.get("kind") != "full":
        sys.exit("The dump must be a full one (Ctrl+F10 or 'ssdump full').")
    return dump


# --- the level file ------------------------------------------------------------------------------------------------


class Level:
    """Objects of one level file: names, tags, local scales, children, and the raw bytes of each MonoBehaviour."""

    def __init__(self, path, pydeps):
        if pydeps:
            sys.path.insert(0, pydeps)
        import UnityPy  # noqa: E402  (found through --pydeps)

        environment = UnityPy.load(path)
        self.objects = {}
        self.transforms = {}
        self.behaviours = []
        for item in environment.objects:
            kind = item.type.name
            if kind == "GameObject":
                data = item.read()
                self.objects[item.path_id] = (data.m_Name, data.m_Tag)
            elif kind == "Transform":
                data = item.read()
                scale = data.m_LocalScale
                self.transforms[item.path_id] = (data.m_GameObject.path_id, data.m_Father.path_id,
                                                 [c.path_id for c in data.m_Children], (scale.x, scale.y, scale.z))
            elif kind == "MonoBehaviour":
                self.behaviours.append(item)
        self.transform_of = {v[0]: k for k, v in self.transforms.items()}
        self._paths = {}

    def path(self, transform):
        """Name path and sibling-index path (from the root down; the root's index is unknown, -1)."""
        if transform in self._paths:
            return self._paths[transform]
        names, indices = [], []
        current = transform
        while current:
            obj, father, _, _ = self.transforms[current]
            names.append(self.objects[obj][0])
            indices.append(self.transforms[father][2].index(current) if father else -1)
            current = father
        result = ("/".join(reversed(names)), [i for i in reversed(indices)])
        self._paths[transform] = result
        return result

    def named(self, name):
        for transform, (obj, _, _, _) in self.transforms.items():
            if self.objects[obj][0] == name:
                yield transform

    def first_untagged(self, transform):
        """The first object below this one, depth first, tagged Untagged (tag 0): what PlayMaker's GetChild with an
        empty name and the tag Untagged finds."""
        for child in self.transforms[transform][2]:
            obj = self.transforms[child][0]
            if self.objects[obj][1] == 0:
                return self.objects[obj][0]
            found = self.first_untagged(child)
            if found is not None:
                return found
        return None

    def fsm_states(self, path, fsm_name, state_names):
        """The printable strings of each state of a PlayMakerFSM, cut at the start of each state's serialized data.
        Used only for states whose actions a dump did not load."""
        wanted = struct.pack("<i", len(fsm_name)) + fsm_name.encode("latin-1")
        results = []
        for item in self.behaviours:
            raw = item.get_raw_data()
            if raw.find(wanted, 0, 120) < 0:
                continue
            obj = int.from_bytes(raw[4:12], "little")
            transform = self.transform_of.get(obj)
            if transform is None or self.path(transform)[0] != path:
                continue
            starts = {}
            for state in state_names:
                starts[state] = find_state_start(raw, state)
            ordered = sorted((at, state) for state, at in starts.items() if at is not None)
            regions = {}
            for i, (at, state) in enumerate(ordered):
                end = ordered[i + 1][0] if i + 1 < len(ordered) else len(raw)
                regions[state] = [s.decode("latin-1") for s in re.findall(rb"[\x20-\x7e]{3,}", raw[at:end])]
            results.append(regions)
        return results


def find_state_start(raw, name):
    """Where a state's serialized data starts: its name, an empty or short description, a colour index and the
    rectangle it is drawn in (a transition's target state is followed by small integers instead)."""
    encoded = name.encode("latin-1")
    pattern = struct.pack("<i", len(encoded)) + encoded
    at = 0
    while True:
        i = raw.find(pattern, at)
        if i < 0:
            return None
        at = i + 1
        p = (i + 4 + len(encoded) + 3) & ~3
        if p + 32 > len(raw):
            continue
        description = struct.unpack_from("<i", raw, p)[0]
        if description < 0 or description > 2000:
            continue
        p = (p + 4 + description + 3) & ~3
        colour = struct.unpack_from("<i", raw, p)[0]
        if not 0 <= colour <= 16:
            continue
        x, y, width, height = struct.unpack_from("<4f", raw, p + 4)
        if 1 <= width <= 2000 and 1 <= height <= 2000:
            return i


# --- paths as the catalogue writes them ----------------------------------------------------------------------------


class Places:
    """Writes a scene path the way the catalogue does: below the part object of an entry ("Entry:rest"), below the
    car's root (no leading '/'), or from the scene's top (leading '/')."""

    def __init__(self):
        self.hosts = {}
        self.parts = {}

    def add_host(self, path, entry):
        self.parts[entry] = path
        if path in self.hosts and self.hosts[path] != entry:
            self.hosts[path] = None  # two entries share the name path (the hubcaps): only their own paths use it
        else:
            self.hosts[path] = entry

    def write(self, path, prefer=None):
        """prefer: the entry the path belongs to, when its part's name path is shared with other entries."""
        if path is None:
            return None
        if ":" in path:
            sys.exit("A path has a ':' in it, which the catalogue uses as a separator: " + path)
        own = self.parts.get(prefer)
        if own and own != "*" and (path == own or path.startswith(own + "/")):
            return prefer + ":" + path[len(own) + 1:]
        best = None
        for host, entry in self.hosts.items():
            if entry is None:
                continue
            if path == host or path.startswith(host + "/"):
                if best is None or len(host) > len(best[0]):
                    best = (host, entry)
        if best is not None:
            return best[1] + ":" + path[len(best[0]) + 1:]
        if path.startswith(CAR + "/"):
            return path[len(CAR) + 1:]
        return "/" + path


# --- assembly triggers -----------------------------------------------------------------------------------------------


IDLE_NAMES = ("Check for Part collision", "Check for Part collision 2", "Check for Part ")


def idle_state(fsm):
    start = fsm.start
    if start == "Check Assembly":
        return fsm.target(start, "FINISHED")
    return start


def setup_variable(fsm):
    """The flag the game sets to assemble without the player when it loads a game: tested in 'Check Assembly' (or in
    the resting state of the wheels and hubcaps) with a BoolTest that sends ASSEMBLE."""
    for state in fsm.states:
        if state != "Check Assembly" and state != idle_state(fsm):
            continue
        for action in fsm.actions(state):
            if action.get("t") == "BoolTest" and (action.get("p") or {}).get("isTrue") == "ASSEMBLE":
                return variable((action.get("p") or {}).get("boolVariable"))
    if "Check Assembly" in fsm.states and "Setup" in fsm.bvars():
        return "Setup"
    return None


def database_entry(path):
    if isinstance(path, str) and path.startswith("Database/"):
        return path[len("Database/"):]
    return None


def state_strings(fsm, state, level):
    if fsm.level is None:
        found = level.fsm_states(fsm.path, fsm.name, list(fsm.states)) if level else []
        fsm.level = found[0] if len(found) == 1 else {}
        if len(found) != 1:
            note("level", "%s :: %s: %d copies in the level file, its unloaded states are not read" % (fsm.path, fsm.name, len(found)))
    return fsm.level.get(state, [])


def installs(fsm, state, level, entries):
    """Entries a state marks installed: a SetFsmBool of Installed = true on a Database entry. For a state the dump did
    not load, a SetFsmBool in it whose strings name Installed and a variable that points at an entry."""
    found = []
    if fsm.loaded(state):
        for action in fsm.actions(state):
            p = action.get("p") or {}
            if action.get("t") != "SetFsmBool" or value(p.get("variableName")) != "Installed" or value(p.get("setValue")) is not True:
                continue
            machine = value(p.get("fsmName"))
            if machine == "Data":
                entry = database_entry(value(p.get("gameObject")))
                if entry is None and variable(p.get("gameObject")):
                    entry = "?" + variable(p.get("gameObject"))  # filled at run time (the hubcaps)
                if entry:
                    found.append(entry)
            elif machine == "Use" and variable(p.get("gameObject")) == "Part":
                found.append("@item")  # a shop item: its own Use machine marks its database entry
        return found, True
    types = [a.get("t") for a in fsm.actions(state)]
    if "SetFsmBool" not in types:
        return found, False
    strings = state_strings(fsm, state, level)
    if "Installed" not in strings:
        return found, False
    for name, path in (fsm.vars.get("gameObject") or {}).items():
        entry = database_entry(path)
        if entry and name in strings and not re.match(r"db_(PartRequired|Req|Required)", name):
            found.append(entry)
    return found, False


def reach(fsm, start, stops):
    paths = {start: [start]}
    queue = [start]
    while queue:
        state = queue.pop(0)
        for _, to in fsm.trans(state):
            if not to or to in paths or to in stops or to not in fsm.states:
                continue
            paths[to] = paths[state] + [to]
            queue.append(to)
    return paths


def click_states(fsm):
    clicks = []
    for state in fsm.states:
        acts = fsm.actions(state)
        if fsm.loaded(state):
            for action in acts:
                if action.get("t") == "GetMouseButtonDown":
                    event = value((action.get("p") or {}).get("sendEvent"))
                    clicks.append((state, event))
        elif any(a.get("t") == "GetMouseButtonDown" for a in acts):
            events = [e for e, _ in fsm.trans(state) if e not in ("LOOP", "STOP")]
            if len(events) == 1:
                clicks.append((state, events[0]))
    return clicks


def chain(fsm, state, limit=12):
    """A state and those that follow it while each has a single way out."""
    out = [state]
    while len(out) < limit:
        ways = [to for _, to in fsm.trans(out[-1]) if to in fsm.states]
        if len(ways) != 1 or ways[0] in out:
            break
        out.append(ways[0])
    return out


def enter_state(fsm, path):
    """The earliest state on the click's path from which the install follows with no decision on the way."""
    enter = path[-1]
    for state in reversed(path[:-1]):
        if len(fsm.trans(state)) == 1:
            enter = state
        else:
            break
    return enter


def plays_sound(fsm, states, level):
    for state in states:
        if fsm.loaded(state):
            for action in fsm.actions(state):
                p = action.get("p") or {}
                if action.get("t") == "MasterAudioPlaySound" and value(p.get("soundGroupName")) == "CarBuilding":
                    return True
        else:
            if any(a.get("t") == "MasterAudioPlaySound" for a in fsm.actions(state)):
                strings = state_strings(fsm, state, level)
                if "CarBuilding" in strings:
                    return True
    return False


def family_of(fsm, states, level):
    kinds = set()
    for state in states:
        if state == "Set data":
            kinds.add("wheel")
        if fsm.loaded(state):
            for action in fsm.actions(state):
                p = action.get("p") or {}
                kind = action.get("t")
                on_part = variable(p.get("gameObject")) == "Part"
                if kind == "DestroyObject" and on_part:
                    kinds.add("A")
                elif kind == "AddComponent" and on_part and value(p.get("component")) in ("HingeJoint", "FixedJoint"):
                    kinds.add("B2")
                elif kind == "DestroyComponent" and on_part and value(p.get("component")) == "Rigidbody":
                    kinds.add("B1")
                elif kind == "SetParent" and on_part:
                    kinds.add("parent")
        else:
            types = [a.get("t") for a in fsm.actions(state)]
            strings = state_strings(fsm, state, level)
            if "DestroyObject" in types:
                kinds.add("A")
            if "AddComponent" in types and ("HingeJoint" in strings or "FixedJoint" in strings):
                kinds.add("B2")
            if "DestroyComponent" in types and "Rigidbody" in strings:
                kinds.add("B1")
            if "SetParent" in types:
                kinds.add("parent")
    for kind in ("wheel", "B2", "B1", "A"):
        if kind in kinds:
            return kind
    return "B1" if "parent" in kinds else "other"


def requirements(fsm, idle, path_states, entry):
    """What a trigger reads from the database before it takes a part: (entry, variable, value it must have)."""
    out = []
    pass_targets = set(path_states)
    for state in fsm.states:
        if state in (idle,) or not fsm.loaded(state):
            continue
        reads = {}
        for action in fsm.actions(state):
            p = action.get("p") or {}
            if action.get("t") == "GetFsmBool" and value(p.get("fsmName")) == "Data":
                other = database_entry(value(p.get("gameObject")))
                if other and variable(p.get("storeValue")):
                    reads[variable(p.get("storeValue"))] = (other, value(p.get("variableName")))
        if not reads:
            continue
        for action in fsm.actions(state):
            p = action.get("p") or {}
            kind = action.get("t")
            if kind == "BoolTest" and variable(p.get("boolVariable")) in reads:
                other, name = reads[variable(p.get("boolVariable"))]
                good = None
                for event, wanted in ((value(p.get("isTrue")), True), (value(p.get("isFalse")), False)):
                    if event and leads_on(fsm, state, event, pass_targets, idle):
                        good = wanted
                if good is not None:
                    out.append((other, name, good))
            elif kind in ("BoolNoneTrue", "BoolAllTrue"):
                names = [variable(v) for v in value(p.get("boolVariables")) or []]
                event = value(p.get("sendEvent"))
                if event and leads_on(fsm, state, event, pass_targets, idle):
                    for v in names:
                        if v in reads:
                            out.append((reads[v][0], reads[v][1], kind == "BoolAllTrue"))
    result = []
    for other, name, good in out:
        if other == entry and name == "Installed" and good is False:
            continue  # "already installed": the slot is free
        item = {"entry": other, "variable": name, "value": good}
        if item not in result:
            result.append(item)
    return sorted(result, key=lambda r: (r["entry"], r["variable"]))


def leads_on(fsm, state, event, targets, idle):
    to = fsm.target(state, event)
    if to is None or to == idle:
        return False
    seen = reach(fsm, to, {idle})
    return any(t in seen for t in targets) or to in targets


def accepted_part(fsm, click, idle):
    """The name of the part the hand must hold on the way to a click state: a GetChild by name, and a StringCompare
    that sends the event leading there."""
    names = []
    # states from which the click state is reached
    for state in fsm.states:
        if not fsm.loaded(state):
            continue
        for action in fsm.actions(state):
            p = action.get("p") or {}
            if action.get("t") == "StringCompare":
                event = value(p.get("equalEvent"))
                if event and fsm.target(state, event) == click:
                    names.append(value(p.get("compareTo")))
    if names:
        return names[0]
    for state in fsm.states:
        if not fsm.loaded(state):
            continue
        for action in fsm.actions(state):
            p = action.get("p") or {}
            if action.get("t") == "GetChild" and variable(p.get("gameObject")) == "ItemPivot":
                return value(p.get("childName"))
    hand = (fsm.vars.get("string") or {}).get("HandChild")
    return hand or None


def analyse_trigger(fsm, level, entries):
    idle = idle_state(fsm)
    clicks = click_states(fsm)
    stops = {idle} | {c for c, _ in clicks} | {s for s in fsm.states if s.startswith("Check for Part")}
    install_states = {}
    unloaded = False
    for state in fsm.states:
        found, read = installs(fsm, state, level, entries)
        if found:
            install_states[state] = found
            if not read:
                unloaded = True
    bindings = []
    for click, event in clicks:
        start = fsm.target(click, event)
        if start is None:
            continue
        paths = reach(fsm, start, stops)
        targets = [s for s in install_states if s in paths]
        if not targets:
            # wheels: nothing is marked installed; the part is parented in the state that follows the click
            targets = [s for s in paths if s.startswith("Assemble")][:1]
        for target in targets:
            path = paths[target]
            enter = enter_state(fsm, path)
            follow = chain(fsm, enter, 20)
            states_after = list(dict.fromkeys(path[path.index(enter):] + follow))
            bindings.append({
                "click": click,
                "install": target,
                "entries": install_states.get(target, []),
                "state": enter,
                "family": family_of(fsm, states_after, level),
                "sound": plays_sound(fsm, states_after, level),
                "part": accepted_part(fsm, click, idle),
                "requires": requirements(fsm, idle, path, None),
                "removalOnPart": any(
                    a.get("t") == "EnableFSM" and variable((a.get("p") or {}).get("gameObject")) == "Part"
                    and value((a.get("p") or {}).get("fsmName")) == "Removal"
                    for s in states_after if fsm.loaded(s) for a in fsm.actions(s)),
                # Where it hangs the part, when that is a fixed object (the battery's "pivot_battery").
                "pivot": next((value((a.get("p") or {}).get("parent")) for s in states_after if fsm.loaded(s) for a in fsm.actions(s)
                               if a.get("t") == "SetParent" and variable((a.get("p") or {}).get("gameObject")) == "Part"
                               and isinstance(value((a.get("p") or {}).get("parent")), str)), None),
            })
    idle_event = set()
    if idle in fsm.states and fsm.target(idle, "ASSEMBLE"):
        reached = reach(fsm, fsm.target(idle, "ASSEMBLE"), {idle})
        idle_event = set(reached)
    for binding in bindings:
        binding["event"] = binding["install"] in idle_event
    if not fsm.all_loaded():
        unloaded = True
    return {"idle": idle, "bindings": bindings, "setup": setup_variable(fsm), "unloaded": unloaded}


# --- the catalogue -------------------------------------------------------------------------------------------------


def build(dump, level):
    fsms = [Fsm(raw) for raw in dump["fsms"]]
    by_name = collections.defaultdict(list)
    for fsm in fsms:
        by_name[fsm.name].append(fsm)

    entries = collections.OrderedDict()
    for fsm in by_name["Data"]:
        name = database_entry(fsm.path)
        if not name or name.split("/")[0] not in GROUPS:
            continue
        if name in entries:
            note("duplicate", "two Data machines at Database/%s; the second is left out" % name)
            continue
        entries[name] = {"fsm": fsm, "name": name}

    places = Places()
    for name, entry in entries.items():
        part = entry["fsm"].gvar("ThisPart")
        if part and not part.startswith(CAR + "/"):
            places.add_host(part, name)
    # a wheel item (a Use machine with a TireType) hosts the hubcap's trigger and its own bolts, wherever it is
    for fsm in by_name["Use"]:
        if "TireType" in (fsm.vars.get("int") or {}):
            places.add_host(fsm.path, "wheel")

    # wheel slots: the game has no entry for them
    wheel_triggers = {}
    for fsm in by_name["Assembly"]:
        m = re.search(r"/TriggerWheel(FL|FR|RL|RR)_New$", fsm.path)
        if m and fsm.path.startswith(CAR + "/"):
            wheel_triggers[m.group(1)] = fsm
    for corner in CORNERS:
        if corner in wheel_triggers:
            entries["Wheels/" + corner] = {"fsm": None, "name": "Wheels/" + corner, "wheel": wheel_triggers[corner]}

    catalogue = collections.OrderedDict()
    for name in entries:
        catalogue[name] = {"name": name, "triggers": [], "removals": [], "boltChecks": [], "blockedBy": []}

    # triggers
    analysed = {}
    for fsm in by_name["Assembly"]:
        analysed[id(fsm)] = (fsm, analyse_trigger(fsm, level, entries))

    trigger_links = collections.defaultdict(set)  # entry -> trigger fsm ids, from the entries' own variables
    for name, entry in entries.items():
        fsm = entry["fsm"]
        if fsm is None:
            continue
        for var, path in (fsm.vars.get("gameObject") or {}).items():
            if var.startswith("Trigger") and var != "Triggers" and path:
                for other in by_name["Assembly"]:
                    if other.path == path:
                        trigger_links[name].add(id(other))
    linked_by = collections.defaultdict(list)
    for name, ids in trigger_links.items():
        for key in ids:
            linked_by[key].append(name)

    for key, (fsm, result) in analysed.items():
        for binding in result["bindings"]:
            targets = [e for e in binding["entries"] if not e.startswith("?") and e != "@item"]
            dynamic = [e for e in binding["entries"] if e.startswith("?")]
            if not targets and "@item" in binding["entries"]:
                own = database_entry(fsm.gvar("db_ThisPart"))
                if own:
                    targets = [own]
                else:
                    targets = sorted(linked_by.get(key, []))
                dynamic = []
            if dynamic:
                # the slot reads which entry from the part it is given (the hubcaps): every entry whose part has that name
                targets = [n for n, e in entries.items() if e["fsm"] is not None and basename(e["fsm"].gvar("ThisPart")) == binding["part"] and not e["fsm"].gvar("Trigger")]
                if not targets:
                    note("trigger", "%s: marks an entry installed through %s and no entry has its part" % (fsm.path, dynamic[0]))
            m = re.search(r"/TriggerWheel(FL|FR|RL|RR)_New$", fsm.path)
            if m and fsm.path.startswith(CAR + "/"):
                targets = ["Wheels/" + m.group(1)]
            if not targets:
                if fsm.path.startswith(CAR + "/") or "CARPARTS" in fsm.path:
                    note("trigger", "%s: the click from '%s' reaches '%s', which marks no entry installed" % (fsm.path, binding["click"], binding["install"]))
                continue
            for target in targets:
                if target not in catalogue:
                    note("trigger", "%s: installs %s, which is not an entry" % (fsm.path, target))
                    continue
                if key in trigger_links[target]:
                    trigger_links[target].discard(key)
                item = collections.OrderedDict()
                item["path"] = places.write(fsm.path)
                item["part"] = binding["part"]
                item["state"] = binding["state"]
                item["event"] = binding["event"]
                item["setup"] = result["setup"]
                item["sound"] = binding["sound"]
                item["family"] = binding["family"]
                item["requires"] = [r for r in binding["requires"] if r["entry"] != target or r["variable"] != "Installed"]
                if result["unloaded"]:
                    item["unloaded"] = True
                if len(targets) > 1:
                    item["shared"] = True
                catalogue[target]["triggers"].append(item)
                catalogue[target].setdefault("_trigger_paths", set()).add(fsm.path)
                if binding["removalOnPart"]:
                    catalogue[target]["_removal_on_part"] = True
                    if binding["pivot"] and binding["part"]:
                        catalogue[target]["_pivot"] = binding["pivot"] + "/" + binding["part"]

    for name, ids in trigger_links.items():
        for key in ids:
            fsm, result = analysed[key]
            if fsm.path in catalogue[name].get("_trigger_paths", set()):
                continue
            note("trigger", "%s names %s as its trigger, and no click there was found to install it (%s)"
                 % (name, fsm.path, "states not loaded in the dump" if result["unloaded"] else "states loaded"))

    # engine mounts: the block has no trigger, it is bolted where the hoist leaves it
    for fsm in by_name["Assembly"]:
        if re.search(r"/_Motor/Trigger_motor\d$", fsm.path) and "DatabaseMotor/Block" in catalogue:
            item = collections.OrderedDict()
            item["path"] = places.write(fsm.path)
            item["part"] = basename(entries["DatabaseMotor/Block"]["fsm"].gvar("ThisPart"))
            item["state"] = "Show bolts" if "Show bolts" in fsm.states else None
            item["event"] = False
            item["setup"] = None
            item["sound"] = False
            item["family"] = "hoist"
            item["requires"] = []
            if not fsm.all_loaded():
                item["unloaded"] = True
            catalogue["DatabaseMotor/Block"]["triggers"].append(item)

    # removals
    for fsm in by_name["Removal"]:
        entry = database_entry(fsm.gvar("db_ThisPart"))
        if entry is None:
            continue
        if entry not in catalogue:
            note("removal", "%s: removes %s, which is not an entry" % (fsm.path, entry))
            continue
        written = places.write(fsm.path, entry)
        if written not in catalogue[entry]["removals"]:
            catalogue[entry]["removals"].append(written)
        for var, path in sorted((fsm.vars.get("gameObject") or {}).items()):
            if not var.startswith("db_RemoveReq"):
                continue
            other = database_entry(path)
            if other is None or other not in catalogue:
                if path:
                    note("removal", "%s: %s is %s, not an entry of the catalogue; left out" % (fsm.path, var, path))
                continue
            reads, loaded = removal_reads(fsm, var, level)
            if reads == "unused":
                note("removal", "%s: %s (%s) is not named in its unloaded 'Requirements' states; left out" % (fsm.path, var, other))
                continue
            if reads is None:
                note("removal", "%s: %s (%s) has no requirement state that reads it; left out" % (fsm.path, var, other))
                continue
            if not loaded:
                note("removal", "%s: %s (%s) read as %s from the level file's strings" % (fsm.path, var, other, reads))
            item = {"entry": other, "variable": reads}
            if item not in catalogue[entry]["blockedBy"]:
                catalogue[entry]["blockedBy"].append(item)
        spawn = fsm.gvar("SpawnPart")
        if spawn and creates_object(fsm) is not False and entries.get(entry) and entries[entry]["fsm"].gvar("SpawnThis"):
            catalogue[entry]["spawn"] = spawn + "(Clone)"

    # Removal machines that name no entry: on a copy that entries switch on (the covers), or on the part itself when
    # the trigger switches the part's Removal on (the shop items: spark plugs, bulbs, belt, oil filter, battery)
    for fsm in by_name["Removal"]:
        if database_entry(fsm.gvar("db_ThisPart")):
            continue
        for name, entry in entries.items():
            if entry["fsm"] is None:
                continue
            for var, path in (entry["fsm"].vars.get("gameObject") or {}).items():
                if re.fullmatch(r"ActivateThis\d?", var) and path == fsm.path:
                    written = places.write(fsm.path)
                    if written not in catalogue[name]["removals"]:
                        catalogue[name]["removals"].append(written)
    for name, item in catalogue.items():
        pivot = item.pop("_pivot", None)
        if not item["removals"] and item.pop("_removal_on_part", False) and name.startswith("PartsStatus/"):
            # On the part itself, found through its record; one whose record does not name it (the battery) is where its
            # trigger hangs it.
            record = entries[name]["fsm"] if name in entries else None
            has_variable = record is not None and (record.gvar("ThisPart") or record.gvar("SpawnThis")
                                                   or any(v.startswith("Current") for v in (record.vars.get("gameObject") or {})))
            if not has_variable and pivot:
                item["removals"].append(places.write(pivot))
                note("removal", "%s: its record names no part; its removal is where its trigger hangs it, %s" % (name, pivot))
            else:
                item["removals"].append(name + ":")
        item.pop("_removal_on_part", None)

    # bolt checks and bolts
    checks = {}
    for fsm in by_name["BoltCheck"]:
        entry = database_entry(fsm.gvar("db_ThisPart"))
        if entry is None or entry not in catalogue:
            if fsm.path.startswith(CAR) or "CARPARTS" in fsm.path:
                note("boltcheck", "%s: no database entry (db_ThisPart %s)" % (fsm.path, fsm.gvar("db_ThisPart")))
            continue
        check = collections.OrderedDict()
        check["path"] = places.write(fsm.path, entry)
        check["yes"] = num(fsm.fvar("BoltedYES"))
        check["no"] = num(fsm.fvar("BoltedNO"))
        check["bolts"] = []
        if check["yes"] is None:
            note("boltcheck", "%s (%s): no BoltedYES; the game cannot mark it bolted by tightness" % (fsm.path, entry))
        checks[fsm.path] = (entry, check)
        catalogue[entry]["boltChecks"].append(check)

    # Tools whose bolts matter to the car: the hoist's hook hangs the engine block once its two bolts are in ("Hook",
    # "Remove hinge"). An entry of their own, with no record in the database and nothing to install: only bolts.
    for name, path in TOOLS.items():
        found = [f for f in by_name["BoltCheck"] if f.path == path]
        if not found:
            note("tool", "%s: no BoltCheck at %s; left out" % (name, path))
            continue
        entries[name] = {"fsm": None, "name": name}
        catalogue[name] = {"name": name, "triggers": [], "removals": [], "boltChecks": [], "blockedBy": []}
        check = collections.OrderedDict()
        check["path"] = places.write(path)
        check["yes"] = num(found[0].fvar("BoltedYES"))
        check["no"] = num(found[0].fvar("BoltedNO"))
        check["bolts"] = []
        checks[path] = (name, check)
        catalogue[name]["boltChecks"].append(check)

    bolts_by_level = match_bolts(by_name["Screw"], level)
    wheel_stages = set()
    for fsm in by_name["Screw"]:
        owner = fsm.gvar("PartAssembled")
        if owner is None:
            continue
        if owner not in checks:
            if places.write(owner) == "wheel:":
                wheel_stages.add(stages_of(fsm))
            else:
                note("bolt", "%s: a bolt of %s, which has no BoltCheck of an entry; left out" % (fsm.path, owner))
            continue
        found = bolts_by_level.get(id(fsm))
        if found is None:
            note("bolt", "%s (%s): not found in the level file; left out" % (fsm.path, fsm.ipath))
            continue
        child, size = found
        if child is None or not re.fullmatch(r"bolt\d+", child):
            note("bolt", "%s: its first untagged child is %r, not bolt<n>; left out" % (fsm.path, child))
            continue
        bolt = collections.OrderedDict()
        bolt["index"] = int(child[-1])  # GetStringRight with charCount 1: the last character
        if len(child) > 5:
            note("bolt", "%s: child %s, so the game's index is %d (only the last digit)" % (fsm.path, child, bolt["index"]))
        entry_of_check = checks[owner][0]
        bolt["path"] = places.write(fsm.path, entry_of_check)
        bolt["size"] = round(size, 3)
        bolt["stages"] = stages_of(fsm)
        checks[owner][1]["bolts"].append(bolt)

    # Wheels: the game renames every wheel to wheel_regula or wheel_offset when it loads, so a dump's wheel cannot be
    # found by name in the level file. Every wheel of the level file is read instead: if all are built alike, that is
    # the wheel slot's bolts. Their tightness is kept by the wheel's own Use machine.
    shapes = collections.defaultdict(list)
    for transform in level.named("BoltPM"):
        path, indices = level.path(transform)
        m = re.match(r"^(.*/wheel_[a-z]+\d*)/(Bolts/BoltPM)$", path)
        if m:
            key = (m.group(1), tuple(indices[:-2]))
            shapes[key].append((level.first_untagged(transform), m.group(2), round(level.transforms[transform][3][0], 3)))
    kinds = collections.Counter(tuple(sorted(v)) for v in shapes.values())
    uses = [f for f in by_name["Use"] if "TireType" in (f.vars.get("int") or {})]
    yes = sorted(set(num(f.fvar("BoltedYES")) for f in uses))
    if len(kinds) == 1 and len(wheel_stages) == 1:
        shape = next(iter(kinds))
        for corner in CORNERS:
            name = "Wheels/" + corner
            if name not in catalogue:
                continue
            check = collections.OrderedDict()
            check["path"] = name + ":"
            check["fsm"] = "Use"
            check["yes"] = yes[0] if len(yes) == 1 else None
            check["no"] = 0
            check["bolts"] = []
            for child, rest, size in shape:
                bolt = collections.OrderedDict()
                bolt["index"] = int(child[-1])
                bolt["path"] = name + ":" + rest
                bolt["size"] = size
                bolt["stages"] = next(iter(wheel_stages))
                check["bolts"].append(bolt)
            catalogue[name]["boltChecks"].append(check)
        note("wheel", "the %d wheels of the level file all have the bolts %s; the wheels' Use machines say BoltedYES %s in "
             "the dump (the save clamps Tightness to 32)" % (len(shapes), [(c, s) for c, _, s in shape], yes))
    else:
        note("wheel", "wheels are not all built alike (%d kinds, stages %s); the wheel slots have no bolts" % (len(kinds), sorted(wheel_stages)))

    # wiring
    for fsm in by_name["Assemble"]:
        entry = database_entry(fsm.gvar("db_ThisPart"))
        if entry is None or entry not in catalogue:
            continue
        wiring = catalogue[entry].setdefault("wiring", collections.OrderedDict([("ends", []), ("state", None), ("activates", [])]))
        wiring["ends"].append(places.write(fsm.path))
        finish = "Finish assembly" if "Finish assembly" in fsm.states else None
        if finish is None:
            note("wiring", "%s: no 'Finish assembly' state" % fsm.path)
        elif fsm.loaded(finish):
            marks = [database_entry(value((a.get("p") or {}).get("gameObject"))) for a in fsm.actions(finish)
                     if a.get("t") == "SetFsmBool" and value((a.get("p") or {}).get("variableName")) == "Installed"
                     and value((a.get("p") or {}).get("setValue")) is True]
            if entry not in marks:
                note("wiring", "%s: 'Finish assembly' does not mark the entry installed" % fsm.path)
            for other in marks:
                if other and other != entry and other in catalogue:
                    by = catalogue[other].setdefault("installedBy", [])
                    if entry not in by:
                        by.append(entry)  # a battery terminal: the wire that ends on it marks it installed too
        else:
            note("wiring", "%s: 'Finish assembly' not loaded in the dump" % fsm.path)
        wiring["state"] = wiring["state"] or finish
        for var, path in sorted((fsm.vars.get("gameObject") or {}).items()):
            if var.startswith("ThisWire") and path:
                written = places.write(path)
                if written not in wiring["activates"]:
                    wiring["activates"].append(written)

    # what each entry is
    for name, item in catalogue.items():
        entry = entries[name]
        fsm = entry["fsm"]
        group = name.split("/")[0]
        item["group"] = "Wheels" if group == "Wheels" else group[len("Database"):] if group.startswith("Database") else group
        if fsm is not None:
            item["data"] = [f for f in DATA_FLAGS if f in fsm.bvars()]
            this, spawn = fsm.gvar("ThisPart"), fsm.gvar("SpawnThis")
            if this:
                item["part"], item["partVariable"] = basename(this), "ThisPart"
            elif spawn:
                item["part"], item["partVariable"] = basename(spawn), "SpawnThis"
            else:
                current = [v for v in (fsm.vars.get("gameObject") or {}) if v.startswith("Current")]
                if len(current) == 1:
                    item["partVariable"] = current[0]  # the shop item in the slot (empty in a dump of a bare car)
            copies = []
            for var, path in sorted((fsm.vars.get("gameObject") or {}).items()):
                if re.fullmatch(r"ActivateThis\d?", var) and path and not path.endswith("/Parts"):
                    copies.append(places.write(path))
            if copies and group != "DatabaseWiring":
                item["copies"] = copies
        else:
            item["data"] = []
            item["part"] = None
        item["family"] = family_for(name, item)
        if item["family"] in ("status", "wheel", "hoist") and item.get("part") is None:
            parts = [t["part"] for t in item["triggers"] if t.get("part")]
            if parts:
                item["part"] = parts[0]
        for trigger in item["triggers"]:
            if not trigger.get("part") and item.get("part"):
                trigger["part"] = item["part"]
                note("trigger", "%s: the part %s takes at %s is the entry's own (%s), not read from the trigger"
                     % (name, trigger["part"], trigger["path"], item.get("partVariable")))

    # dependencies that name a missing entry
    for name, item in catalogue.items():
        for trigger in item["triggers"]:
            for r in trigger["requires"]:
                if r["entry"] not in catalogue:
                    note("requires", "%s: requires %s, which is not an entry" % (name, r["entry"]))
        for b in item["blockedBy"]:
            if b["entry"] not in catalogue:
                note("removal", "%s: blocked by %s, which is not an entry" % (name, b["entry"]))

    return catalogue, entries


def family_for(name, item):
    group = name.split("/")[0]
    if group == "DatabaseWiring":
        return "wiring"
    if group == "PartsStatus":
        return "status"
    if group == "Wheels":
        return "wheel"
    if name == "DatabaseMotor/Block":
        return "hoist"
    kinds = sorted(set(t["family"] for t in item["triggers"] if t["family"] not in ("hoist",)))
    if not kinds:
        return "other"
    if len(kinds) > 1:
        note("family", "%s: its triggers do different things (%s); the entry says %s" % (name, ", ".join(kinds), kinds[0]))
    return kinds[0]


def num(v):
    if v is None:
        return None
    return int(v) if float(v).is_integer() else round(float(v), 4)


def stages_of(fsm):
    numbers = [int(m.group(1)) for s in fsm.states for m in [re.fullmatch(r"(\d) 2", s)] if m]
    if not numbers:
        numbers = [int(s) for s in fsm.states if re.fullmatch(r"\d", s)]
    return max(numbers) if numbers else None


def removal_reads(fsm, var, level):
    """Which variable of the entry a removal's requirement reads: from its loaded actions, or from the level file's
    strings of its unloaded 'Requirements' states (the variable name, "Data", then the variable read). "unused" when
    the requirement state does not name it at all."""
    unloaded = []
    for state in fsm.states:
        if not state.startswith("Requirements"):
            continue
        if not fsm.loaded(state):
            unloaded.append(state)
            continue
        for action in fsm.actions(state):
            p = action.get("p") or {}
            if action.get("t") == "GetFsmBool" and variable(p.get("gameObject")) == var:
                return value(p.get("variableName")), True
    # A state's parameters are stored by kind: first every action's object (here the variables that point at the
    # entries), then every action's texts (here "Data" and the variable read). The k-th GetFsmBool reads the k-th pair.
    named = False
    objects = set((fsm.vars.get("gameObject") or {}).keys())
    for state in unloaded:
        strings = state_strings(fsm, state, level)
        reads = sum(1 for a in fsm.actions(state) if a.get("t") == "GetFsmBool")
        owners = [s for s in strings if s in objects]
        pairs = [strings[i + 1] for i in range(len(strings) - 1)
                 if strings[i] == "Data" and strings[i + 1] in ("Installed", "Bolted")]
        if var in owners:
            named = True
            if len(owners) == len(pairs) == reads:
                return pairs[owners.index(var)], False
    if unloaded and not named:
        return "unused", False
    return None, False


def creates_object(fsm):
    for state in fsm.states:
        if state.startswith("Remove part") and fsm.loaded(state):
            return any(a.get("t") == "CreateObject" for a in fsm.actions(state))
    return None


def match_bolts(screws, level):
    """Each bolt of the dump in the level file: the name of its first untagged child (bolt<n>) and its local scale x.
    A bolt is matched by its name path and then by the longest run of equal sibling indices from the bottom; the
    root's index is never compared (a running game orders roots its own way)."""
    names = set(basename(f.path) for f in screws if f.gvar("PartAssembled") is not None)
    candidates = collections.defaultdict(list)
    for transform, (obj, _, _, _) in level.transforms.items():
        if level.objects[obj][0] not in names:
            continue
        path, indices = level.path(transform)
        candidates[path].append((indices, transform))
        tail = path.split("/")
        for k in range(1, len(tail)):
            candidates["*/" + "/".join(tail[k:])].append((indices, transform))
    result = {}
    for fsm in screws:
        if fsm.gvar("PartAssembled") is None:
            continue
        indices = [int(i) for i in fsm.ipath.split("/")] if fsm.ipath else []
        pool = candidates.get(fsm.path)
        if not pool:
            parts = fsm.path.split("/")
            for k in range(1, len(parts)):
                pool = candidates.get("*/" + "/".join(parts[k:]))
                if pool:
                    break
        if not pool:
            continue
        best, score = None, -1
        for other, transform in pool:
            s = 0
            for a, b in zip(reversed(indices[1:]), reversed(other[1:])):
                if a != b:
                    break
                s += 1
            if s > score:
                best, score = transform, s
        result[id(fsm)] = (level.first_untagged(best), level.transforms[best][3][0])
    return result


# --- writing -------------------------------------------------------------------------------------------------------


KEY_ORDER = ("name", "group", "family", "data", "part", "partVariable", "copies", "spawn", "triggers", "removals",
             "blockedBy", "boltChecks", "wiring", "installedBy")


def finish(catalogue):
    out = []
    for name in sorted(catalogue, key=lambda n: n.encode("utf-8")):
        item = catalogue[name]
        item.pop("_trigger_paths", None)
        unique = []
        for trigger in item["triggers"]:
            if trigger not in unique:
                unique.append(trigger)  # the same trigger on several wheels, written the same way
        item["triggers"] = unique
        item["triggers"].sort(key=lambda t: (t["path"], t["state"] or "", t["part"] or ""))
        item["removals"].sort()
        item["blockedBy"].sort(key=lambda b: (b["entry"], b["variable"]))
        item["boltChecks"].sort(key=lambda c: c["path"])
        for check in item["boltChecks"]:
            check["bolts"].sort(key=lambda b: (b["index"], b["path"]))
        if "installedBy" in item:
            item["installedBy"].sort()
        if "wiring" in item:
            item["wiring"]["ends"].sort()
            item["wiring"]["activates"].sort()
        ordered = collections.OrderedDict()
        for key in KEY_ORDER:
            v = item.get(key)
            if v is None or v == [] or v == {}:
                if key in ("name", "group", "family"):
                    ordered[key] = v
                continue
            ordered[key] = v
        for trigger in ordered.get("triggers", []):
            for key in [k for k, v in trigger.items() if v is None or v == []]:
                del trigger[key]
        out.append(ordered)
    return out


def write_json(value, indent=0):
    """JSON with small objects and arrays of plain values on one line."""
    text = json.dumps(value, ensure_ascii=False)
    if len(text) + indent <= 150 or not isinstance(value, (dict, list)):
        return text
    pad = " " * (indent + 2)
    if isinstance(value, list):
        return "[\n" + ",\n".join(pad + write_json(v, indent + 2) for v in value) + "\n" + " " * indent + "]"
    return "{\n" + ",\n".join(pad + json.dumps(k) + ": " + write_json(v, indent + 2) for k, v in value.items()) + "\n" + " " * indent + "}"


def main():
    parser = argparse.ArgumentParser(description="Builds the Satsuma's part catalogue from a full dump and the level file.")
    parser.add_argument("dump")
    parser.add_argument("level")
    parser.add_argument("-o", "--out", required=True)
    parser.add_argument("--notes")
    parser.add_argument("--pydeps")
    args = parser.parse_args()

    dump = load_dump(args.dump)
    level = Level(args.level, args.pydeps)
    catalogue, entries = build(dump, level)
    items = finish(catalogue)
    document = collections.OrderedDict([("format", FORMAT), ("car", CAR), ("entries", items)])
    with open(args.out, "w", encoding="utf-8", newline="\n") as stream:
        stream.write(write_json(document) + "\n")

    counts = collections.Counter(i["family"] for i in items)
    triggers = sum(len(i.get("triggers", [])) for i in items)
    bolts = sum(len(c["bolts"]) for i in items for c in i.get("boltChecks", []))
    wires = sum(len(i["wiring"]["ends"]) // 2 for i in items if "wiring" in i)
    summary = "%d entries (%s); %d triggers; %d bolts; %d wiring pairs" % (
        len(items), ", ".join("%s %d" % kv for kv in sorted(counts.items())), triggers, bolts, wires)
    print(summary)
    review = review_lines(items)
    if args.notes:
        with open(args.notes, "w", encoding="utf-8", newline="\n") as stream:
            stream.write("Satsuma part catalogue: notes of the generator (tools/satsuma/make-parts.py)\n")
            stream.write("Dump: %s\nLevel file: %s\n%s\n\n" % (args.dump, args.level, summary))
            stream.write("== Entries to confirm ==\n")
            for line in review:
                stream.write(line + "\n")
            stream.write("\n== Everything the generator noticed ==\n")
            for kind, text in sorted(set(notes)):
                stream.write("[%s] %s\n" % (kind, text))
    else:
        for line in review:
            print(line)
        for kind, text in sorted(set(notes)):
            print("[%s] %s" % (kind, text))


def review_lines(items):
    """The entries whose data is incomplete or uncertain, one line each."""
    lines = []
    for item in items:
        name = item["name"]
        for trigger in item.get("triggers", []):
            if trigger.get("unloaded"):
                lines.append("%s: trigger %s was not loaded in the dump; state '%s', family %s, sound and requirements come "
                             "from the level file and the state graph" % (name, trigger["path"], trigger["state"], trigger["family"]))
            if item.get("part") and trigger.get("part") != item["part"] and item["family"] not in ("wheel",):
                lines.append("%s: trigger %s takes '%s', the entry's part is '%s'" % (name, trigger["path"], trigger.get("part"), item["part"]))
        if item["family"] == "wiring" and "wiring" not in item and not item.get("installedBy"):
            lines.append("%s: a wiring entry with no connector pair in the dump that marks it installed" % name)
        if item["family"] == "other":
            lines.append("%s: family other: %s" % (name, "its trigger neither destroys nor parents the part" if item.get("triggers")
                                                   else "no trigger installs it"))
        if item["family"] in ("A", "B1", "B2", "status") and not item.get("removals"):
            lines.append("%s: no Removal machine found" % name)
        for check in item.get("boltChecks", []):
            seen = collections.Counter(b["index"] for b in check["bolts"])
            for index, count in sorted(seen.items()):
                if count > 1 and item["family"] != "wheel":
                    paths = [b["path"] for b in check["bolts"] if b["index"] == index]
                    lines.append("%s: %d bolts share the game's index %d (%s): they read and write the same stage in the "
                                 "save" % (name, count, index, ", ".join(paths)))
            if check.get("yes") is None:
                lines.append("%s: its bolt check %s has no BoltedYES" % (name, check["path"]))
        if item["family"] == "status":
            lines.append("%s: the shop item in the slot is found through %s (empty in a dump of a bare car)"
                         % (name, item.get("partVariable") or "the trigger's pivot (no variable)"))
    return lines


if __name__ == "__main__":
    main()
