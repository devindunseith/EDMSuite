# deceleration_init.py - sets up the IronPython environment ready for scripting
# the deceleration control software.

import clr
import sys
from System.IO import Path

# Import the edm control software assemblies into IronPython
sys.path.append(Path.GetFullPath("..\\ScanMaster\\bin\\Debug\\"))
clr.AddReferenceToFile("ScanMaster.exe")
sys.path.append(Path.GetFullPath("..\\DecelerationHardwareControl\\bin\\Debug\\"))
clr.AddReferenceToFile("DecelerationHardwareControl.exe")
clr.AddReferenceToFile("DAQ.dll")
clr.AddReferenceToFile("SharedCode.dll")

# Load some system assemblies that we'll need
clr.AddReference("System.Drawing")
clr.AddReference("System.Windows.Forms")
clr.AddReference("System.Xml")

# code for IronPython remoting problem workaround
class typedproxy(object):
    __slots__ = ['obj', 'proxyType']
    def __init__(self, obj, proxyType):
        self.obj = obj
        self.proxyType = proxyType
    def __getattribute__(self, attr):
        proxyType = object.__getattribute__(self, 'proxyType')
        obj = object.__getattribute__(self, 'obj')
        return getattr(proxyType, attr).__get__(obj, proxyType)


# create connections to the control programs
import System
import ScanMaster
import DecelerationHardwareControl

sm = typedproxy(System.Activator.GetObject(ScanMaster.Controller, 'tcp://localhost:1170/controller.rem'), ScanMaster.Controller)
hc = typedproxy(System.Activator.GetObject(DecelerationHardwareControl.Controller, 'tcp://localhost:1172/controller.rem'), DecelerationHardwareControl.Controller)

# usage message
print('Deceleration interactive scripting control')
print('''
The variables sm and hc are pre-assigned to the ScanMaster and
EDMHardwareControl Controller objects respectively. You can call any
of these objects' methods, for example: sm.AcquireAndWait(5). Look at the
c# code to see which remote methods are available. You can use any Python
code you like to script these calls.

You can run scripts in the DecelerationScripts directory with the command
run(i), where i is the script's index number (below). For this to work the
script must have a run_script() function defined somewhere. You'd be unwise
to try and run more than one script in a session with this method!

Available scripts:''')

# script shortcuts
import nt
pp = Path.GetFullPath("..\\DecelerationScripts")
files = nt.listdir(pp)
scriptsToLoad = [e for e in files if e.EndsWith(".py") and e != "deceleration_init.py" and e != "winforms.py"]
for i in range(len(scriptsToLoad)):
            print str(i) + ": " + scriptsToLoad[i]
print ""

def run(i):
	execfile(scriptsToLoad[i])
	run_script()
