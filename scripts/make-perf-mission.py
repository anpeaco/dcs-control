"""Make a repeatable perf-test copy of Golan_Valley_of_Tears_v5.miz.

- The air-start F-15E (Uzi 3-1) is the single-player slot ("Player"), alone in its group, starting at
  4000 m / 200 m/s heading north along y = -8300, over both kill boxes; one waypoint at the far end.
- Enforced options: immortal, unlimited fuel.
- The autopilot is engaged by cockpit triggers: UFC PB 9 (A/P menu), PB 3 (ALT HOLD), A/P key
  (heading hold + altitude hold with wings level).
- The random helicopter dice (trigger 13) are fixed: both Hinds at T+1 min, no Hip.

usage: python -I miz_build_perf.py <source.miz> <output.miz>
"""
import sys
import zipfile

import lupa

src, dst = sys.argv[1], sys.argv[2]
zin = zipfile.ZipFile(src)
L = lupa.LuaRuntime()
L.execute(zin.read("mission").decode("utf-8"))

L.execute(r'''
local function copy(t)
  if type(t) ~= 'table' then return t end
  local c = {}
  for k, v in pairs(t) do c[k] = copy(v) end
  return c
end

-- The air-start player group.
local grp
for _, side in pairs(mission.coalition) do for _, c in pairs(side.country or {}) do for _, cat in pairs(c) do
  if type(cat) == 'table' and cat.group then
    for _, g in pairs(cat.group) do if g.groupId == 154 then grp = g end end
  end
end end end
assert(grp and grp.name == 'Player - F-15E (air start)', 'air-start group not found')
local u = grp.units[1]
assert(u.name == 'Uzi 3-1' and u.type == 'F-15ESE', 'unexpected first unit')
grp.units = { u }
u.skill = 'Player'
local X0, X1, Y, ALT, SPD = -252000, -150000, -8300, 4000, 200
u.x, u.y, u.alt, u.speed, u.heading, u.psi = X0, Y, ALT, SPD, 0, 0
grp.x, grp.y = X0, Y
local p1 = grp.route.points[1]
p1.x, p1.y, p1.alt, p1.speed, p1.name = X0, Y, ALT, SPD, 'PERF START'
local p2 = copy(p1)
p2.x, p2.name = X1, 'PERF END'
p2.task = { id = 'ComboTask', params = { tasks = {} } }
p2.ETA, p2.ETA_locked = 0, false
grp.route.points = { p1, p2 }

-- Enforced options.
mission.forcedOptions = mission.forcedOptions or {}
mission.forcedOptions.immortal = true
mission.forcedOptions.fuel = true

-- Fixed dice (trigger 13 rolled them at start).
local rule = mission.trigrules[13]
assert(rule.comment:find('random dice'), 'trigger 13 is not the dice')
local template = copy(mission.trigrules[19].actions[1])
assert(template.predicate == 'a_set_flag_value', 'trigger 19 is not a flag set')
local fixed = { {101, 30}, {102, 30}, {103, 99}, {104, 1}, {111, 1}, {112, 1}, {113, 3} }
rule.actions = {}
local compiled = {}
for i, fv in ipairs(fixed) do
  local a = copy(template)
  a.flag, a.value = fv[1], fv[2]
  rule.actions[i] = a
  compiled[#compiled + 1] = string.format('a_set_flag_value(%d, %d);', fv[1], fv[2])
end
rule.comment = rule.comment .. ' (PERF TEST: fixed, both Hinds at T+1 min, no Hip)'
assert(mission.trig.actions[13]:find('a_set_flag_random(101', 1, true), 'trigger 13 compiled differently')
mission.trig.actions[13] = table.concat(compiled, '')
startup13 = tostring(mission.trig.funcStartup[13])

-- Autopilot by cockpit clickable actions: front UFC (device 56).
local UFC, PB9, PB3, AP = 56, 3009, 3003, 3034
local steps = {
  {5, PB9, 1, 'PERF: UFC PB9 (A/P menu) press'},
  {6, PB9, 0, 'PERF: UFC PB9 release'},
  {7, PB3, 1, 'PERF: UFC PB3 (ALT HOLD) press'},
  {8, PB3, 0, 'PERF: UFC PB3 release'},
  {9, AP, 1, 'PERF: UFC A/P key press (engage)'},
  {10, AP, 0, 'PERF: UFC A/P key release'},
}
local n = #mission.trigrules
local function add(rulecond, seconds, action, compiledAction, comment)
  n = n + 1
  mission.trigrules[n] = { predicate = 'triggerOnce', comment = comment, eventlist = '',
    rules = { { predicate = 'c_time_after', seconds = seconds } }, actions = { action } }
  mission.trig.actions[n] = compiledAction .. string.format(' mission.trig.func[%d]=nil;', n)
  mission.trig.conditions[n] = string.format('return(c_time_after(%d) )', seconds)
  mission.trig.flag[n] = true
  mission.trig.func[n] = string.format('if mission.trig.conditions[%d]() then mission.trig.actions[%d]() end', n, n)
end
for _, s in ipairs(steps) do
  add(nil, s[1], { predicate = 'a_cockpit_perform_clickable_action', cockpit_device = UFC, command = s[2], value = s[3] },
    string.format('a_cockpit_perform_clickable_action(%d, %d, %d);', UFC, s[2], s[3]), s[4])
end
local note = "PERF TEST: autopilot engaged (heading + altitude hold). The UFC should read A/P ALT. "
  .. "If not, press the UFC A/P key (keyboard A). Keep the throttle in the same place every run."
local script = string.format('trigger.action.outText(%q, 20)', note)
add(nil, 12, { predicate = 'a_do_script', text = script },
  string.format('a_do_script(%q);', script), 'PERF: autopilot note')

-- Serialize in DCS's own layout, keys sorted (numbers first) so the output is stable.
local function key(k)
  if type(k) == 'number' then return string.format('[%d]', k) end
  return string.format('[%q]', k)
end
local function value(v)
  local t = type(v)
  if t == 'string' then return string.format('%q', v) end
  if t == 'number' then
    if math.type(v) == 'integer' then return string.format('%d', v) end
    if v == math.floor(v) and math.abs(v) < 2^53 then return string.format('%d', math.floor(v)) end
    return string.format('%.17g', v)
  end
  return tostring(v)
end
local function sorted(t)
  local ks = {}
  for k in pairs(t) do ks[#ks + 1] = k end
  table.sort(ks, function(a, b)
    if type(a) == type(b) then return a < b end
    return type(a) == 'number'
  end)
  return ks
end
function ser(t, ind, out)
  out[#out + 1] = '\n' .. ind .. '{'
  for _, k in ipairs(sorted(t)) do
    local v = t[k]
    if type(v) == 'table' then
      out[#out + 1] = '\n' .. ind .. '\t' .. key(k) .. '='
      ser(v, ind .. '\t', out)
      out[#out + 1] = ','
    else
      out[#out + 1] = '\n' .. ind .. '\t' .. key(k) .. '=' .. value(v) .. ','
    end
  end
  out[#out + 1] = '\n' .. ind .. '}'
end
function serialize_mission()
  local out = { 'mission = ' }
  ser(mission, '', out)
  return table.concat(out) .. '\n'
end

local function same(a, b)
  if type(a) ~= type(b) then return false end
  if type(a) ~= 'table' then return a == b end
  for k, v in pairs(a) do if not same(v, b[k]) then return false end end
  for k in pairs(b) do if a[k] == nil then return false end end
  return true
end
function check(text)
  local env = {}
  local f = assert(load(text, 'mission', 't', env))
  f()
  return same(env.mission, mission)
end
''')

text = L.eval("serialize_mission()")
assert L.eval("check")(text), "round trip differs"

with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as zout:
    for info in zin.infolist():
        data = text.encode("utf-8") if info.filename == "mission" else zin.read(info.filename)
        zout.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED)
print("written", dst)
