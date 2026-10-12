"""Checks make-perf-mission.py's script timing under Lua 5.1 (DCS's), with stub DCS tables: every scheduled function
and event handler call is bracketed by "PERFT>n name" / "PERFT<n" lines, return values and errors pass through, and
--no-timing leaves the mission as it was.

usage: python -I scripts/test-perf-mission-timing.py <Golan_Valley_of_Tears_v5.miz>
"""
import os
import subprocess
import sys
import tempfile
import zipfile

import lupa
from lupa import lua51

source = sys.argv[1]
make = os.path.join(os.path.dirname(os.path.abspath(__file__)), "make-perf-mission.py")
with tempfile.TemporaryDirectory() as tmp:
    timed, plain = os.path.join(tmp, "timed.miz"), os.path.join(tmp, "plain.miz")
    subprocess.run([sys.executable, "-I", make, source, timed], check=True, stdout=subprocess.DEVNULL)
    subprocess.run([sys.executable, "-I", make, source, plain, "--no-timing"], check=True, stdout=subprocess.DEVNULL)
    M = lupa.LuaRuntime()
    M.execute(zipfile.ZipFile(timed).read("mission").decode("utf-8"))
    P = lupa.LuaRuntime()
    P.execute(zipfile.ZipFile(plain).read("mission").decode("utf-8"))

assert M.eval("#mission.trigrules[58].actions") == 2 and P.eval("#mission.trigrules[58].actions") == 1
assert M.eval("mission.trigrules[58].actions[2].text") == P.eval("mission.trigrules[58].actions[1].text"), "script changed"
compiled = M.eval("mission.trig.actions[58]")
assert compiled.endswith(P.eval("mission.trig.actions[58]")), "compiled script changed"
assert compiled.startswith('a_do_script("-- PERF TEST script timing')

L = lua51.LuaRuntime()
L.execute(r'''
LOG, QUEUE, HANDLERS = {}, {}, {}
env = { info = function(s) LOG[#LOG + 1] = s end }
timer = { scheduleFunction = function(f, arg, t) QUEUE[#QUEUE + 1] = { f = f, arg = arg }; return #QUEUE end }
world = { addEventHandler = function(h) HANDLERS[#HANDLERS + 1] = h end }
function a_do_script(text) assert(loadstring(text))() end
''')
# The trigger's first action, in the compiled form DCS runs.
L.execute(compiled[: len(compiled) - len(P.eval("mission.trig.actions[58]"))])
L.execute(r'''
timer.scheduleFunction(function(a, time) return a * 10 end, 7, 1.0)
timer.scheduleFunction(function() error('boom') end, nil, 2.0)
local H = {}
function H:onEvent(e) SEEN = e.id end
world.addEventHandler(H)
R1 = QUEUE[1].f(QUEUE[1].arg, 1.0)
OK2, ERR2 = pcall(QUEUE[2].f, nil, 2.0)
HANDLERS[1]:onEvent({ id = 23 })
''')
log = list(L.eval("LOG").values())
assert L.eval("R1") == 70, "return value lost"
assert L.eval("OK2") is False and str(L.eval("ERR2")).endswith("boom"), "error not passed through"
assert L.eval("SEEN") == 23, "event not delivered"
assert log[0] == "PERFT timing on", log
assert log[1].startswith("PERFT>1 T ") and log[2] == "PERFT<1", log
assert log[3].startswith("PERFT>2 T ") and log[4] == "PERFT<2", log
assert log[5].startswith("PERFT>3 E23 ") and log[6] == "PERFT<3", log
print("ok")
