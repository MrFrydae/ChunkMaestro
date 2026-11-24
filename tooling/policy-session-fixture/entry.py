"""Isolated Python bootstrap: fixed root-owned directory, no environment import path."""
import runpy
import sys
sys.dont_write_bytecode = True
sys.path.insert(0, '/opt/cm14-fixture')
runpy.run_path('/opt/cm14-fixture/process.py', run_name='__main__')
