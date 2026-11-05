# SOLIDWORKS 2026 SP4 interop assemblies

Copied unmodified from `SOLIDWORKS\api\redist` of SOLIDWORKS 2026 SP4 (revision 34.4.0, assembly version 34.4.0.78).
`redist.txt` beside them is SOLIDWORKS' own statement that these files may be redistributed unmodified, subject to
the SOLIDWORKS license terms.

They are compile-time references only (`Private=false`): SOLIDWORKS loads its own copies at run time, and nothing
this repository builds or ships contains them. To build against a local installation instead, pass
`-p:SolidWorksInteropDir=<path to api\redist>\`.
