# flat_master_dropin.py
# Flat Master Orchestrator (PixInsight) — directory-based scan & run
# - GUI: PySide6. Optional FITS header reads via astropy.
# - PixInsight 1.9.3 PJSR template: robust assignment & save logic.
#
# Requires: pip install PySide6 astropy
# Tested on: Windows 11 + PixInsight 1.9.3
#
# CHANGE (2025-10-21):
#   - Skip exposure groups with <3 flats; if a directory has no such groups, it is left out.
#   - Write a session .log file mirroring the GUI log (path announced on startup).

import os, sys, re, json, tempfile, subprocess, threading, shutil, time
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed

from PySide6.QtWidgets import (
    QApplication, QWidget, QVBoxLayout, QHBoxLayout, QPushButton, QFileDialog,
    QLineEdit, QLabel, QTreeView, QPlainTextEdit, QListWidget, QListWidgetItem,
    QSplitter, QMessageBox, QGroupBox, QFormLayout, QMenu, QFileSystemModel, QCheckBox
)
from PySide6.QtGui import QStandardItemModel, QStandardItem, QAction
from PySide6.QtCore import Qt, QDir
from PySide6.QtWidgets import QHeaderView

# --- optional: astropy for FITS ---
try:
    from astropy.io import fits
    _HAS_ASTROPY = True
except Exception:
    _HAS_ASTROPY = False

# ---------- Tunables ----------
DEFAULT_PI_EXE = r"C:\Program Files\PixInsight\bin\PixInsight.exe"
FILE_EXTS      = {".xisf",".fits",".fit"}
MAX_HEADER_BYTES = 4 * 1024 * 1024  # unchanged
N_THREADS = max(4, (os.cpu_count() or 8))

# Skip these dir names (case-insensitive) when walking
SKIP_DIR_PATTERNS = {"_darkmasters", "_calibratedflats", "masters"}
# Ignore existing masters when deciding if a folder “has flats”
MASTER_RE = re.compile(r"^MasterFlat_.*\.xisf$", re.IGNORECASE)

# ---------- PJSR template for PixInsight 1.9.3 ----------
PJSR_TEMPLATE = r"""
// === Flat Master Executor (PixInsight 1.9.x) ===
// Injected config from Python:
var CFG = JSON.parse(%CONFIG_JSON_HERE%);

// -------- helpers --------
function log(s){ Console.writeln(s); }
function warn(s){ Console.warningln(s); }
function joinPath(a,b){ if(!a)return b; var slash=(a.indexOf("\\")>=0)?"\\":"/"; if(a.endsWith("/")||a.endsWith("\\"))return a+b; return a+slash+b; }
function parentDir(p){ if (!p) return ""; var i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\")); return i>0 ? p.substring(0,i) : ""; }
function ensureDir(p){ if (p && !File.directoryExists(p)) File.createDirectory(p, true); }
function kexp(x){ return (Math.round(x*1000)/1000).toString(); }
function baseName(p){ return p.replace(/^.*[\\/]/,''); }
function touch(p, s){ try{ if(!p) return; var f=new File; f.createForWriting(p); if(s) f.outTextLn(s); f.close(); }catch(e){} }

// enum fetch with fallbacks (defensive across builds)
function enumVal(klass, names, defVal){
  for (var i=0;i<names.length;i++){
    var n = names[i];
    try{ if (typeof klass[n] === "number") return klass[n]; }catch(e){}
    try{ if (klass.prototype && typeof klass.prototype[n] === "number") return klass.prototype[n]; }catch(e){}
  }
  return defVal;
}
var II_ENUM = {
  Comb_Average:  enumVal(ImageIntegration, ["Average"], 0),
  Weight_Dont:   enumVal(ImageIntegration, ["DontCare","Weight_DontCare"], 0),
  Norm_None:     enumVal(ImageIntegration, ["NoNormalization","NoScale","NoScaling"], 0),
  Norm_Mult:     enumVal(ImageIntegration, ["Multiplicative"], enumVal(ImageIntegration, ["NoNormalization"], 0)),
  Rej_None:      enumVal(ImageIntegration, ["NoRejection"], 0),
  Rej_Winsor:    enumVal(ImageIntegration, ["WinsorizedSigmaClipping","WinsorizedSigmaClip","WinsorizedSigma"], enumVal(ImageIntegration, ["NoRejection"], 0)),
  Rej_PC:        enumVal(ImageIntegration, ["PercentileClip","Percentile"], enumVal(ImageIntegration, ["NoRejection"], 0)),
  Rej_LinFit:    enumVal(ImageIntegration, ["LinearFit"], enumVal(ImageIntegration, ["NoRejection"], 0)),
  RejNorm_None:  enumVal(ImageIntegration, ["NoRejectionNormalization","NoNormalization"], 0),
  RejNorm_Eq:    enumVal(ImageIntegration, ["EqualizeFluxes","RejectionNormalization_EqualizeFluxes"], enumVal(ImageIntegration, ["NoRejectionNormalization","NoNormalization"], 0))
};
// ImageIntegration.images must be table rows: [enabled, path, drizzlePath, localNormalizationDataPath]
function assignIIImagesRowsPaths(II, paths){
  var rows = [];
  for (var i=0;i<paths.length;i++) rows.push([ true, String(paths[i]), "", "" ]);
  II.images = rows;
  log("  [II.images rows; n="+paths.length+"]");
}

// ImageCalibration.targetFrames: rows [enabled, path]
function assignICTargets(IC, paths){
  var rows = [];
  for (var i=0;i<paths.length;i++) rows.push([true, String(paths[i])]);
  IC.targetFrames = rows;
}

// robust save
function saveXISF(win, outPath, hints){
  ensureDir(parentDir(outPath));
  var tried = [];
  function tryCall(label, fn){ try{ fn(); log("  [saveAs "+label+"] "+outPath); return true; }catch(e){ tried.push(label+": "+e); return false; } }
  if (tryCall("path,format,hints",            function(){ win.saveAs(outPath, "xisf", hints); })) return;
  if (tryCall("path,format,hints,overwrite",  function(){ win.saveAs(outPath, "xisf", hints, false); })) return;
  if (tryCall("path,format",                  function(){ win.saveAs(outPath, "xisf"); })) return;
  if (tryCall("path,overwrite,format,hints",  function(){ win.saveAs(outPath, false, "xisf", hints); })) return;
  if (tryCall("path,overwrite,ask,format,hints", function(){ win.saveAs(outPath, false, false, "xisf", hints); })) return;
  if (tryCall("path,overwrite",               function(){ win.saveAs(outPath, false); })) return;
  if (tryCall("path-only",                    function(){ win.saveAs(outPath); })) return;
  throw new Error("saveAs failed for "+outPath+" ; tried => "+tried.join(" | "));
}

function safeNum(x){ return (x===undefined||x===null||isNaN(x)) ? NaN : Number(x); }
function metaScore(c,want,match){
  var s=0;
  var cg=safeNum(c.gain),   wg=safeNum(want.gain);
  var co=safeNum(c.offset), wo=safeNum(want.offset);
  var ct=safeNum(c.temp),   wt=safeNum(want.temp);
  if(match.enforceBinning && want.binning && c.binning && c.binning===want.binning) s+=3;
  if(match.preferSameGainOffset){
    if(!isNaN(cg) && !isNaN(wg) && Math.abs(cg-wg) < 0.01) s+=2;
    if(!isNaN(co) && !isNaN(wo) && Math.abs(co-wo) < 0.5 ) s+=2;
  }
  if(match.preferClosestTemp && !isNaN(ct) && !isNaN(wt)){
    var dt=Math.abs(ct - wt);
    if (dt <= match.maxTempDeltaC) s += (1.5 - dt*0.2);
  }
  return s;
}

function groupByExp(cats, typeName){
  var m = {};
  for (var i=0;i<cats.length;i++){
    var c=cats[i]; if (c.type!==typeName) continue;
    var k=kexp(c.exposure); (m[k]=m[k]||[]).push(c);
  }
  return m;
}

function integrateToMaster(paths,outPath,forDark,hints,rej){
  if(!paths.length) throw new Error("No frames for "+outPath);
  if(paths.length < 3) throw new Error("ImageIntegration needs >=3 inputs; got "+paths.length);

  var II=new ImageIntegration;
  assignIIImagesRowsPaths(II, paths);

  // common setup (unchanged from your flow)
  II.combination = II_ENUM.Comb_Average;
  II.weightMode  = II_ENUM.Weight_Dont;
  II.evaluateNoise = false;
  II.generate64BitResult = true;

  // keep memory usage low (as you wanted)
  II.generateRejectionMaps = false;
  II.generateSlopeMaps     = false;
  II.generateIntegratedImage = true;

  // Normalization & rejection normalization for flats match WBPP
  if(forDark){
    II.normalization = II_ENUM.Norm_None;
    II.rejection     = II_ENUM.Rej_Winsor;              // darks default to winsor
    II.rejectionNormalization = II_ENUM.RejNorm_None;
  } else {
    II.normalization = II_ENUM.Norm_Mult;               // WBPP: flats multiplicative
    II.rejectionNormalization = II_ENUM.RejNorm_Eq;     // WBPP: equalize fluxes for flats

    // --- WBPP rejection selection for flats, based on frame count ---
    var n = paths.length;
    if (n < 6) {
      // PercentileClip
      II.rejection = II_ENUM.Rej_PC;
      II.pcClipLow  = 0.20;
      II.pcClipHigh = 0.10;
    } else if (n <= 15) {
      // WinsorizedSigmaClipping
      II.rejection = II_ENUM.Rej_Winsor;
      II.sigmaLow  = 4.0;
      II.sigmaHigh = 3.0;
      II.winsorizationCutoff = 5.0;
      II.clipLow  = true;
      II.clipHigh = true;
    } else {
      // LinearFit clipping
      II.rejection = II_ENUM.Rej_LinFit;
      II.linearFitLow  = 5.0;
      II.linearFitHigh = 4.0;
      II.clipLow  = true;
      II.clipHigh = true;
    }

    // Optional large-scale rejection knobs WBPP exposes for flats.
    II.largeScaleClipHigh = false;
  }

  // keep your sigma from 'rej' only when we are actually using sigma-based rejection
  if (II.rejection === II_ENUM.Rej_Winsor) {
    if (rej && typeof rej.lowSigma === "number")  II.sigmaLow  = rej.lowSigma;
    if (rej && typeof rej.highSigma === "number") II.sigmaHigh = rej.highSigma;
  }

  if(!II.executeGlobal()) throw new Error("ImageIntegration failed: "+outPath);

  // Save and close integration window
  var id=II.integrationImageId; 
  var win=ImageWindow.windowById(id); 
  if(!win) throw new Error("Integration window not found");
  // Tag master type in metadata (FITS IMAGETYP) before saving
  try {
    var imgType = (!forDark)
      ? "Master Flat"
      : (outPath.toLowerCase().indexOf("masterdarkflat") >= 0 ? "Master Dark Flat" : "Master Dark");

    var kws = win.keywords;
    if (!kws) kws = [];
    kws.push(new FITSKeyword("IMAGETYP", imgType, "Type of image"));
    win.keywords = kws;
  } catch (e) {
    Console.warningln("[metadata] IMAGETYP not set: " + e);
  }

  saveXISF(win, outPath, hints);
  win.forceClose();

  // defensively close any maps if PI generated them anyway
  var extraIds = [ II.lowRejectionMapImageId, II.highRejectionMapImageId, II.slopeMapImageId ];
  for (var i=0;i<extraIds.length;i++){
    var eid = extraIds[i];
    if (eid && typeof eid === "string" && eid.length){
      var ew = ImageWindow.windowById(eid);
      if (ew) try{ ew.forceClose(); }catch(_){}
    }
  }
}


function calibrateFlats(paths,outDir,masterDarkPath,optimize,hints){
  ensureDir(outDir);
  var IC=new ImageCalibration;
  assignICTargets(IC, paths);

  IC.masterBiasEnabled  = false;
  IC.masterFlatEnabled  = false;
  IC.masterDarkEnabled  = true;
  IC.masterBiasPath     = "";
  IC.masterFlatPath     = "";
  IC.masterDarkPath     = masterDarkPath;
  IC.optimizeDarks      = !!optimize;

  IC.outputDirectory=outDir; IC.outputExtension=".xisf"; IC.outputPostfix="_c"; IC.outputHints=hints;
  if(!IC.executeGlobal()) throw new Error("ImageCalibration failed.");
}

function pickDarkFor(exp, want, cacheDir, cats, rej, hintsCal, match){
  var MDF = groupByExp(cats, "MASTERDARKFLAT");
  var MD  = groupByExp(cats, "MASTERDARK");
  var DF  = groupByExp(cats, "DARKFLAT");
  var D   = groupByExp(cats, "DARK");
  var k = kexp(exp);

  if (MDF[k] && MDF[k].length){
    var best=MDF[k][0], bestS=-1;
    for (var i=0;i<MDF[k].length;i++){ var s=metaScore(MDF[k][i],want,match); if (s>bestS){bestS=s; best=MDF[k][i];}}
    return {path:best.path, optimize:false, kind:"MasterDarkFlat(exact)"};
  }
  if (DF[k] && DF[k].length){
    var out=joinPath(cacheDir,"MasterDarkFlat_"+k+"s.xisf");
    integrateToMaster(DF[k].map(function(x){return x.path;}),out,true,hintsCal,rej);
    return {path:out, optimize:false, kind:"MasterDarkFlat(built)"};
  }
  if (MD[k] && MD[k].length){
    var best2=MD[k][0], s2=-1;
    for (var j=0;j<MD[k].length;j++){ var sc=metaScore(MD[k][j],want,match); if (sc>s2){s2=sc; best2=MD[k][j];}}
    return {path:best2.path, optimize:false, kind:"MasterDark(exact)"};
  }
  if (D[k] && D[k].length){
    var out2=joinPath(cacheDir,"MasterDark_"+k+"s.xisf");
    integrateToMaster(D[k].map(function(x){return x.path;}),out2,true,hintsCal,rej);
    return {path:out2, optimize:false, kind:"MasterDark(built)"};
  }
  if (CFG.allowNearestExposureWithOptimize){
    var allMD=[], kk;
    for (kk in MD){ for (var a=0;a<MD[kk].length;a++) allMD.push(MD[kk][a]); }
    for (kk in D){
      var out3=joinPath(cacheDir,"MasterDark_"+kk+"s.xisf");
      if (!File.exists(out3)){
        integrateToMaster(D[kk].map(function(x){return x.path;}),out3,true,hintsCal,rej);
      }
      allMD.push({path:out3, exposure:parseFloat(kk)});
    }
    if (allMD.length){
      allMD.sort(function(a,b){ return Math.abs(a.exposure-exp)-Math.abs(b.exposure-exp); });
      var best3=allMD[0];
      return {path:best3.path, optimize:true, kind:"MasterDark(nearest+optimize)"};
    }
  }
  return null;
}

// -------- naming helpers --------
function guessFilterFrom(files, dir){
  var rx = /(?:^|[_\-])(?:FILTER|Filter)[_\-]?([A-Za-z0-9]+)/;
  for (var i=0;i<files.length;i++){
    var m = baseName(files[i]).match(rx);
    if (m && m[1]) return String(m[1]).toUpperCase();
  }
  var parts = dir.replace(/\\/g,"/").split("/");
  var last = parts.length ? parts[parts.length-1] : "";
  if (last && !/^\d{4}-\d{2}-\d{2}$/.test(last)) return last.toUpperCase();
  return "UNKNOWN";
}
function guessDateFromPath(dir, files){
  var rx = /\b(20\d{2}-\d{2}-\d{2})\b/;
  var m = dir.match(rx);
  if (m) return m[1];
  for (var i=0;i<files.length;i++){
    var mm = files[i].match(rx);
    if (mm) return mm[1];
  }
  return "UNKNOWNDATE";
}

// -------- main --------
function run(){
  Console.show();
  var plan = CFG.plan || [];
  var cats = CFG.darkCatalog || [];
  var rej = CFG.rejection || {lowSigma:5.0, highSigma:5.0};
  var hintsCal = CFG.xisfHintsCal||"";
  var hintsMaster= CFG.xisfHintsMaster||"";
  var match = CFG.match || {};

  for (var j=0;j<plan.length;j++){
    var job=plan[j], dir=job.dirPath; log("\n=== FLAT dir: "+dir+" ===");
    var rel = job.relDir || ""; if (rel === ".") rel = "";
    var outRoot = job.outRoot || dir;
    var outBase = rel ? joinPath(outRoot, rel) : outRoot;
    ensureDir(outBase);
    for (var g=0; g<job.groups.length; g++){
      var grp=job.groups[g], exp=grp.exposure, files=grp.files, want=grp.want || {};
      if (want.binning === undefined) want.binning = null;
      if (want.gain    === undefined) want.gain    = null;
      if (want.offset  === undefined) want.offset  = null;
      if (want.temp    === undefined) want.temp    = null;

      log("  Exposure "+kexp(exp)+" s : "+files.length+" flats");
      var cacheDir = joinPath(dir, CFG.cacheDirName||"_DarkMasters"); // keep cache next to originals to avoid duplication
      var sel = pickDarkFor(exp, want, cacheDir, cats, rej, hintsCal, match); 
      if(!sel) throw new Error("No suitable dark @ "+kexp(exp)+" s in "+dir);
      log("  Using ["+sel.kind+"] optimize="+sel.optimize);

      // Calibrated flats live under the mirrored processed tree:
      var calOut = joinPath(outBase, (CFG.calibratedSubdirBase||"_CalibratedFlats")+"_"+kexp(exp)+"s");
      calibrateFlats(files, calOut, sel.path, sel.optimize, hintsCal);

      // Collect calibrated products:
      var calFiles=[], ff=new FileFind;
      if(ff.begin(joinPath(calOut,"*.xisf"))){ do{ if(ff.isFile) calFiles.push(joinPath(calOut,ff.name)); }while(ff.next()); }
      ff.end();
      if(!calFiles.length) throw new Error("No calibrated flats in "+calOut);

      // Name master and save it directly in the mirrored folder (no 'Masters' subdir):
      var dateStr = guessDateFromPath(dir, calFiles);
      var filt    = guessFilterFrom(calFiles, dir);
      var masterName = "MasterFlat_" + dateStr + "_" + filt + "_" + kexp(exp) + "s.xisf";
      var masterOut = joinPath(outBase, masterName);
      integrateToMaster(calFiles, masterOut, false, hintsMaster, rej);
      log("  Saved: "+masterOut);

      if (CFG.deleteCalibrated){
        try{
          var delFF=new FileFind;
          if (delFF.begin(joinPath(calOut,"*"))){
            do{
              var p = joinPath(calOut, delFF.name);
              try { if (delFF.isFile) File.remove(p); } catch(e){}
            } while (delFF.next());
          }
          delFF.end();
          try { File.removeDirectory(calOut, true); } catch(e){}
          log("  [cleanup] removed "+calOut);
        }catch(e){
          warn("  [cleanup] failed "+calOut+" : "+e);
        }
      }
    }
  }
  log("\nAll done.");
  touch(CFG.sentinelPath, "OK");
}

try{ run(); }catch(e){ Console.criticalln("ERROR: "+e); touch(CFG.sentinelPath, "ERROR: "+e); throw e; }
"""

# --------------- Header readers (FITS + XISF) ---------------
_FITS_KEYS_EXPTIME = ["EXPTIME","EXPOSURE","EXPOSURETIME","X_EXPOSURE"]
_FITS_KEYS_BIN     = ["XBINNING","BINNING","CCDBINNING","BINNING_MODE"]
_FITS_KEYS_GAIN    = ["GAIN","EGAIN"]
_FITS_KEYS_OFFSET  = ["OFFSET","BLACKLEVEL"]
_FITS_KEYS_TEMP    = ["CCD-TEMP","CCD_TEMP","SENSOR_TEMP","SENSOR-TEMP"]

def _coerce_float(v):
    try:
        if v is None: return None
        if isinstance(v, str) and v.startswith("'") and v.endswith("'"):
            v = v.strip("'")
        return float(v)
    except Exception:
        return None

# --- fast fallback: infer exposure from filename (no header read needed) ---
_EXPOSURE_NAME_RES = [
    re.compile(r'(?<![A-Za-z])(\d+(?:\.\d+)?)\s*s(?=[_\- \.]|$)', re.IGNORECASE),
    re.compile(r'EXPOSURE[_\-=: ]?(\d+(?:\.\d+)?)', re.IGNORECASE),
    re.compile(r'(?<![A-Za-z])S(?:IN)?\s*(\d+(?:\.\d+)?)\s*s(?=[_\- \.]|$)', re.IGNORECASE),
]

def _infer_exposure_from_name(path: str) -> float | None:
    name = os.path.basename(path)
    for rx in _EXPOSURE_NAME_RES:
        m = rx.search(name)
        if m:
            try:
                return float(m.group(1))
            except Exception:
                pass
    return None

def _fits_meta(path: str):
    if not _HAS_ASTROPY:
        return {"exposure": _infer_exposure_from_name(path), "binning": None, "gain": None, "offset": None, "temp": None}
    try:
        with fits.open(path, memmap=False) as hdul:
            hdr = hdul[0].header
            def get(keys):
                for k in keys:
                    if k in hdr:
                        return hdr.get(k)
                return None
            ex = _coerce_float(get(_FITS_KEYS_EXPTIME))
            if ex is None:
                ex = _infer_exposure_from_name(path)
            bn = get(_FITS_KEYS_BIN)
            if bn is not None:
                bn = str(bn).upper()
            gn = _coerce_float(get(_FITS_KEYS_GAIN))
            of = _coerce_float(get(_FITS_KEYS_OFFSET))
            tp = _coerce_float(get(_FITS_KEYS_TEMP))
            return {"exposure": ex, "binning": bn, "gain": gn, "offset": of, "temp": tp}
    except Exception:
        return {"exposure": _infer_exposure_from_name(path), "binning": None, "gain": None, "offset": None, "temp": None}

_XISF_CLOSE_RE = re.compile(r"</\s*(?:\w+:)?XISF\s*>", re.IGNORECASE)
_TAG_FITSKEY   = "FITSKeyword"
_TAG_PROP      = "Property"

def _xisf_header_xml(path: str) -> str | None:
    try:
        with open(path, "rb") as f:
            buf = bytearray()
            chunk = f.read(512*1024)
            while chunk:
                buf += chunk
                if len(buf) > MAX_HEADER_BYTES:
                    break
                try:
                    text = buf.decode("utf-8", errors="ignore")
                except Exception:
                    text = None
                if text and _XISF_CLOSE_RE.search(text):
                    var_m = _XISF_CLOSE_RE.search(text)
                    return text[:var_m.end()]
                chunk = f.read(512*1024)
    except Exception:
        return None
    return None

def _xisf_meta(path: str):
    xml = _xisf_header_xml(path)
    if not xml:
        return {"exposure": _infer_exposure_from_name(path), "binning": None, "gain": None, "offset": None, "temp": None}
    try:
        import xml.etree.ElementTree as ET
        root = ET.fromstring(xml)
        def iter_by_local(local):
            for elem in root.iter():
                tag = elem.tag
                if isinstance(tag, str) and tag.rsplit("}",1)[-1].rsplit(":",1)[-1] == local:
                    yield elem
        vals = {}
        for k in iter_by_local(_TAG_FITSKEY):
            name = (k.attrib.get("name") or k.attrib.get("keyword") or "").upper()
            val  = k.attrib.get("value")
            if not val and k.text: val = k.text
            if not name: continue
            vals[name] = val
        props = {}
        for p in iter_by_local(_TAG_PROP):
            pid = (p.attrib.get("id") or "").upper()
            pv  = p.attrib.get("value")
            if pv is None and p.text: pv = p.text
            if pid: props[pid] = pv
        def pick(keys, alt_props):
            for k in keys:
                if k in vals: return vals[k]
            for k in alt_props:
                for pid,val in props.items():
                    if k in pid: return val
            return None
        ex = _coerce_float(pick(_FITS_KEYS_EXPTIME, ["EXPOSURE","EXPTIME"]))
        if ex is None:
            ex = _infer_exposure_from_name(path)
        bn = pick(_FITS_KEYS_BIN, ["BINNING"])
        if bn is not None: bn = str(bn).upper()
        gn = _coerce_float(pick(_FITS_KEYS_GAIN, ["GAIN"]))
        of = _coerce_float(pick(_FITS_KEYS_OFFSET, ["OFFSET","BLACKLEVEL"]))
        tp = _coerce_float(pick(_FITS_KEYS_TEMP, ["TEMP"]))
        return {"exposure": ex, "binning": bn, "gain": gn, "offset": of, "temp": tp}
    except Exception:
        return {"exposure": _infer_exposure_from_name(path), "binning": None, "gain": None, "offset": None, "temp": None}

def read_meta(path: str):
    ext = Path(path).suffix.lower()
    if ext in {".fits",".fit"}:
        return _fits_meta(path)
    elif ext == ".xisf":
        return _xisf_meta(path)
    else:
        return {"exposure": _infer_exposure_from_name(path), "binning": None, "gain": None, "offset": None, "temp": None}

# ---------- GUI ----------
class FlatMasterApp(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Flat Master Orchestrator (PixInsight) — Directory-based scan")

        # Filesystem browser (show drives root)
        self.fsModel = QFileSystemModel()
        self.fsModel.setFilter(QDir.AllDirs | QDir.NoDotAndDotDot | QDir.Drives)

        self.fsTree = QTreeView()
        self.fsTree.setModel(self.fsModel)
        root_idx = self.fsModel.setRootPath("")  # "Computer"/drives root on Windows
        self.fsTree.setRootIndex(root_idx)
        self.fsTree.setHeaderHidden(False)
        hdr = self.fsTree.header()
        hdr.setStretchLastSection(False)
        hdr.setSectionsMovable(True)
        hdr.setSectionResizeMode(0, QHeaderView.Interactive)
        hdr.setSectionResizeMode(3, QHeaderView.ResizeToContents)
        self.fsTree.setColumnHidden(1, True)
        self.fsTree.setColumnHidden(2, True)
        self.fsTree.setColumnWidth(0, 520)
        self.fsTree.setTextElideMode(Qt.ElideNone)
        self.fsTree.setSortingEnabled(True)
        self.fsTree.sortByColumn(0, Qt.AscendingOrder)
        self.fsTree.setContextMenuPolicy(Qt.CustomContextMenu)
        self.fsTree.customContextMenuRequested.connect(self._fs_context_menu)

        self.addBaseBtn = QPushButton("Add Selected Folder as Base Root (FLATS)")
        self.addBaseBtn.clicked.connect(self.add_base_from_tree)

        self.baseList = QListWidget()
        self.rmBaseBtn = QPushButton("Remove Selected Base")
        self.rmBaseBtn.clicked.connect(self.remove_base_root)

        self.darkListRoots = QListWidget()
        self.addDarkBtn   = QPushButton("Add Dark Root… (recursive)")
        self.addDarkBtn.clicked.connect(self.add_dark_root)
        self.rmDarkBtn    = QPushButton("Remove Selected Dark Root")
        self.rmDarkBtn.clicked.connect(self.remove_dark_root)

        # Settings
        self.piExeEdit = QLineEdit(DEFAULT_PI_EXE)
        self.piExeBtn  = QPushButton("PixInsight.exe…")
        self.piExeBtn.clicked.connect(self.pick_pi)
        self.delCalibChk = QCheckBox("Delete calibrated flats after master is saved")
        self.delCalibChk.setChecked(True)
        self.masterSubdirEdit = QLineEdit("Masters")  # retained, but not used in 'mirrored' mode

        settingsBox = QGroupBox("Settings")
        form = QFormLayout()
        form.addRow("PixInsight.exe:", self._h(self.piExeEdit, self.piExeBtn))
        form.addRow("Master subfolder:", self.masterSubdirEdit)
        form.addRow(self.delCalibChk)
        settingsBox.setLayout(form)

        # Left column
        leftLayout = QVBoxLayout()
        leftLayout.addWidget(QLabel("Filesystem"))
        leftLayout.addWidget(self.fsTree, 3)
        leftLayout.addWidget(self.addBaseBtn)
        leftLayout.addWidget(QLabel("FLAT base roots (recursively scanned for directories containing FITS/XISF):"))
        leftLayout.addWidget(self.baseList)
        leftLayout.addWidget(self.rmBaseBtn)
        leftLayout.addWidget(QLabel("Dark library roots (explicit):"))
        leftLayout.addWidget(self.darkListRoots)
        leftLayout.addWidget(self._h(self.addDarkBtn, self.rmDarkBtn))
        leftLayout.addWidget(settingsBox)
        leftWrap = QWidget(); leftWrap.setLayout(leftLayout)

        # Right column: flats + darks + log
        self.scanBtn        = QPushButton("Scan Selected Bases (flats only)")
        self.selAllBtn      = QPushButton("Select All Dirs")
        self.deselAllBtn    = QPushButton("Deselect All Dirs")
        self.runBtn         = QPushButton("Run Selected")

        self.treeFlats      = QTreeView()
        self.modelFlats     = QStandardItemModel()
        self.modelFlats.setHorizontalHeaderLabels(["Dir / Exposure Groups / Files"])
        self.treeFlats.setModel(self.modelFlats)
        self.treeFlats.setAlternatingRowColors(True)
        self.treeFlats.header().setSectionResizeMode(0, QHeaderView.Interactive)
        self.treeFlats.setColumnWidth(0, 900)

        # Dark inventory
        self.scanDarksBtn   = QPushButton("Refresh Dark Inventory")
        self.selAllDarksBtn = QPushButton("Select All Darks")
        self.deselAllDarksBtn= QPushButton("Deselect All Darks")
        self.treeDarks      = QTreeView()
        self.modelDarks     = QStandardItemModel()
        self.modelDarks.setHorizontalHeaderLabels(["Dark Type / Exposure / Files"])
        self.treeDarks.setModel(self.modelDarks)
        self.treeDarks.setAlternatingRowColors(True)
        self.treeDarks.header().setSectionResizeMode(0, QHeaderView.Interactive)
        self.treeDarks.setColumnWidth(0, 900)
        self._dark_guard = False
        self.modelDarks.itemChanged.connect(self._dark_item_changed)

        # Log
        self.logBox = QPlainTextEdit(); self.logBox.setReadOnly(True)

        # Wiring
        self.scanBtn.clicked.connect(self.scan_flats)
        self.selAllBtn.clicked.connect(lambda: self._set_all_dirs(True))
        self.deselAllBtn.clicked.connect(lambda: self._set_all_dirs(False))
        self.scanDarksBtn.clicked.connect(self.scan_darks)
        self.selAllDarksBtn.clicked.connect(lambda: self._set_all_darks(True))
        self.deselAllDarksBtn.clicked.connect(lambda: self._set_all_darks(False))
        self.runBtn.clicked.connect(self.runSelected)

        rightLayout = QVBoxLayout()
        rightLayout.addWidget(self._h(self.scanBtn, self.selAllBtn, self.deselAllBtn, self.runBtn))
        rightLayout.addWidget(QLabel("Discovered flat directories (true exposure groups):"))
        rightLayout.addWidget(self.treeFlats, 3)
        rightLayout.addWidget(self._h(self.scanDarksBtn, self.selAllDarksBtn, self.deselAllDarksBtn))
        rightLayout.addWidget(QLabel("Dark Inventory (true exposures; tick to allow):"))
        rightLayout.addWidget(self.treeDarks, 2)
        rightLayout.addWidget(QLabel("PixInsight log:"))
        rightLayout.addWidget(self.logBox)
        rightWrap = QWidget(); rightWrap.setLayout(rightLayout)

        split = QSplitter()
        split.addWidget(leftWrap)
        split.addWidget(rightWrap)
        split.setStretchFactor(1, 2)

        root = QVBoxLayout()
        root.addWidget(split)
        self.setLayout(root)
        self.resize(1650, 940)

        self.plan = []
        self.dark_catalog = []

        # CHANGE: session logfile (created at startup; path announced once)
        self._log_fh = None
        self.log_file_path = None
        try:
            ts = time.strftime("%Y%m%d_%H%M%S")
            self.log_file_path = str(Path(tempfile.gettempdir()) / f"FlatMaster_{ts}.log")
            self._log_fh = open(self.log_file_path, "a", encoding="utf-8", newline="")
            # Announce where logs go
            self.log(f"[logging] session log file: {self.log_file_path}")
        except Exception as e:
            # Fallback to silent failure; GUI log still works
            self._log_fh = None
            self.log(f"[logging] failed to create session log file: {e}")

    # ---------- helpers ----------
    def _h(self, *widgets):
        w = QWidget(); l = QHBoxLayout(w)
        for x in widgets: l.addWidget(x)
        l.addStretch(1); l.setContentsMargins(0,0,0,0)
        return w

    def _fs_context_menu(self, pos):
        idx = self.fsTree.indexAt(pos)
        if not idx.isValid(): return
        menu = QMenu(self)
        act = QAction("Add as Base Root (FLATS)", self)
        act.triggered.connect(self.add_base_from_tree)
        menu.addAction(act)
        resetAct = QAction("Auto-size Columns", self)
        resetAct.triggered.connect(self._autosize_fs_columns)
        menu.addAction(resetAct)
        menu.exec(self.fsTree.viewport().mapToGlobal(pos))

    def _autosize_fs_columns(self):
        self.fsTree.resizeColumnToContents(3)
        self.fsTree.setColumnWidth(0, max(520, self.fsTree.columnWidth(0)))

    def add_base_from_tree(self):
        idx = self.fsTree.currentIndex()
        if not idx.isValid(): return
        path = self.fsModel.filePath(idx)
        if not Path(path).exists(): return
        self.baseList.addItem(QListWidgetItem(path))

    def remove_base_root(self):
        for it in self.baseList.selectedItems():
            self.baseList.takeItem(self.baseList.row(it))

    def add_dark_root(self):
        d = QFileDialog.getExistingDirectory(self, "Add a Dark Library root (recursive)")
        if d:
            self.darkListRoots.addItem(QListWidgetItem(d))

    def remove_dark_root(self):
        for it in self.darkListRoots.selectedItems():
            self.darkListRoots.takeItem(self.darkListRoots.row(it))

    def pick_pi(self):
        f, _ = QFileDialog.getOpenFileName(self, "Select PixInsight.exe", filter="PixInsight.exe (PixInsight.exe)")
        if f:
            self.piExeEdit.setText(f)

    def log(self, text):
        self.logBox.appendPlainText(text)
        # CHANGE: mirror GUI log into a .log file
        try:
            if self._log_fh:
                self._log_fh.write(text + "\n")
                self._log_fh.flush()
        except Exception:
            pass

    # ---------- scanning ----------
    def _scan_files_meta_threaded(self, paths: list[str]) -> dict[str, dict]:
        out = {}
        def task(p):
            return p, read_meta(p)
        with ThreadPoolExecutor(max_workers=N_THREADS) as ex:
            futs = [ex.submit(task, p) for p in paths]
            for f in as_completed(futs):
                p, meta = f.result()
                out[p] = meta
        return out

    def _is_skip_dir(self, name: str) -> bool:
        return name.lower() in SKIP_DIR_PATTERNS or name.startswith(".")

    def _dir_image_files(self, dir_path: str) -> list[str]:
        files = []
        try:
            for fn in os.listdir(dir_path):
                p = os.path.join(dir_path, fn)
                if not os.path.isfile(p):
                    continue
                ext = os.path.splitext(fn)[1].lower()
                if ext not in FILE_EXTS:
                    continue
                if MASTER_RE.match(fn):
                    continue  # ignore previously-integrated masters
                files.append(p)
        except PermissionError:
            pass
        return sorted(files)

    def _iter_candidate_files(self, roots: list[str], label: str):
        cnt_dirs = 0
        for dr in roots:
            for r, dirs, files in os.walk(dr):
                dirs[:] = [d for d in dirs if not self._is_skip_dir(d)]
                cnt_dirs += 1
                if cnt_dirs % 50 == 0:
                    self.log(f"[scan {label}] visited {cnt_dirs} dirs…")
                for fn in files:
                    ext = os.path.splitext(fn)[1].lower()
                    if ext in FILE_EXTS:
                        yield os.path.join(r, fn)

    def scan_flats(self):
        base_roots = [self.baseList.item(i).text() for i in range(self.baseList.count())]
        if not base_roots:
            QMessageBox.information(self, "No base roots", "Add one or more FLAT base roots from the filesystem tree.")
            return

        self.modelFlats.removeRows(0, self.modelFlats.rowCount())
        rootItem = QStandardItem("Flat directories (per dir integration)")
        rootItem.setEditable(False)
        self.modelFlats.appendRow(rootItem)

        self.plan = []
        self.dark_catalog = []  # unchanged here
        total_files = 0
        seen_dirs = set()

        m = {"dirs":0,"pruned":0,"total_listed":0,"with_exp":0,"missing_exp":0,"dirs_skipped_lt3":0,"groups_skipped_lt3":0}
        t0=time.time()

        for base in base_roots:
            for r, dirs, files in os.walk(base):
                m["dirs"] += 1
                before=len(dirs)
                dirs[:] = [d for d in dirs if not self._is_skip_dir(d)]
                m["pruned"] += (before-len(dirs))
                flat_files = self._dir_image_files(r)
                m["total_listed"] += len(files)
                if not flat_files:
                    continue
                if r in seen_dirs:
                    continue
                seen_dirs.add(r)

                meta_map = self._scan_files_meta_threaded(flat_files)
                bins = {}
                wants = {}
                for p in flat_files:
                    ex = meta_map[p].get("exposure")
                    if ex is None:
                        m["missing_exp"] += 1
                        continue
                    k = f"{round(ex,3):.3f}"
                    bins.setdefault(k, []).append(p)
                # derive wants from a representative file in each bin
                for k, plist in bins.items():
                    m0 = meta_map[plist[0]]
                    wants[k] = {
                        "binning": m0.get("binning"),
                        "gain": float(m0["gain"]) if m0.get("gain") is not None else None,
                        "offset": float(m0["offset"]) if m0.get("offset") is not None else None,
                        "temp": float(m0["temp"]) if m0.get("temp") is not None else None,
                    }

                # CHANGE: filter out groups with <3 files
                bins_filtered = {}
                for k, plist in bins.items():
                    if len(plist) >= 3:
                        bins_filtered[k] = plist
                    else:
                        m["groups_skipped_lt3"] += 1
                        self.log(f"[scan flats] skipping exposure group {k}s in {r} (only {len(plist)} files)")

                if not bins_filtered:
                    m["dirs_skipped_lt3"] += 1
                    self.log(f"[scan flats] skipping directory {r} (no exposure group with >=3 flats)")
                    continue

                dirItem = QStandardItem(r)
                dirItem.setEditable(False)
                dirItem.setCheckable(True)
                dirItem.setCheckState(Qt.Checked)
                rootItem.appendRow([dirItem])

                groups = []
                for k in sorted(bins_filtered.keys(), key=lambda s: float(s)):
                    gItem = QStandardItem(f"{k}s  ({len(bins_filtered[k])} files)")
                    gItem.setEditable(False)
                    dirItem.appendRow([gItem])
                    for p in bins_filtered[k]:
                        c = QStandardItem(os.path.basename(p))
                        c.setEditable(False)
                        gItem.appendRow([c])
                    groups.append({"exposure": float(k), "files": bins_filtered[k], "want": wants[k]})

                self.plan.append({"dirPath": r, "groups": groups})
                total_files += len(flat_files)
                m["with_exp"] += sum(len(v) for v in bins_filtered.values())

        self.treeFlats.expandAll()
        dt=time.time()-t0
        self.log(f"FLATS: dirs={m['dirs']}, pruned={m['pruned']}, total_listed={m['total_listed']}, "
                 f"with_exp={m['with_exp']}, missing_exp={m['missing_exp']}; "
                 f"flat_dirs={len(self.plan)}; groups_skipped_lt3={m['groups_skipped_lt3']}, "
                 f"dirs_skipped_lt3={m['dirs_skipped_lt3']}; indexed≈{total_files}; {dt:.2f}s")

    # ---------- darks ----------
    def _classify_dark_type(self, name: str) -> str:
        u = name.upper()
        if "MASTERDARKFLAT" in u: return "MASTERDARKFLAT"
        if "MASTERDARK" in u:     return "MASTERDARK"
        if "DARKFLAT" in u:       return "DARKFLAT"
        if "DARK" in u:           return "DARK"
        return ""

    def scan_darks(self):
        dark_roots = [self.darkListRoots.item(i).text() for i in range(self.darkListRoots.count())]
        self.modelDarks.removeRows(0, self.modelDarks.rowCount())
        root = QStandardItem("Dark Inventory (explicit roots only)")
        root.setEditable(False)
        self.modelDarks.appendRow(root)

        if not dark_roots:
            self.dark_catalog = []
            self.log("Dark Inventory — no roots found (add one or more Dark roots).")
            return

        cand = list(self._iter_candidate_files(dark_roots, "darks"))
        self.log(f"[scan darks] candidate files (ext match) = {len(cand)}")

        meta_map = self._scan_files_meta_threaded(cand)

        catalog = []
        with_exp = 0
        for p in cand:
            typ = self._classify_dark_type(os.path.basename(p))
            if not typ: continue
            mm = meta_map.get(p, {})
            ex = mm.get("exposure")
            if ex is None: 
                continue
            with_exp += 1
            catalog.append({
                "path": p,
                "type": typ,
                "exposure": float(ex),
                "binning": mm.get("binning"),
                "gain": float(mm["gain"]) if mm.get("gain") is not None else None,
                "offset": float(mm["offset"]) if mm.get("offset") is not None else None,
                "temp": float(mm["temp"]) if mm.get("temp") is not None else None
            })

        by_type = {"MASTERDARKFLAT":{}, "MASTERDARK":{}, "DARKFLAT":{}, "DARK":{}}
        for d in catalog:
            k = f"{d['exposure']:.3f}s"
            by_type[d["type"]].setdefault(k, []).append(d)

        total = 0
        self._dark_guard = True
        for typ in ["MASTERDARKFLAT","MASTERDARK","DARKFLAT","DARK"]:
            typItem = QStandardItem(typ); typItem.setEditable(False); typItem.setCheckable(True); typItem.setCheckState(Qt.Checked)
            root.appendRow([typItem])
            for kexp in sorted(by_type[typ].keys(), key=lambda x: float(x[:-1])):
                expItem = QStandardItem(kexp); expItem.setEditable(False); expItem.setCheckable(True); expItem.setCheckState(Qt.Checked)
                typItem.appendRow([expItem])
                for d in sorted(by_type[typ][kexp], key=lambda dd: dd["path"].lower()):
                    it = QStandardItem(d["path"])
                    it.setEditable(False)
                    it.setCheckable(True)
                    it.setCheckState(Qt.Checked)
                    expItem.appendRow([it])
                    total += 1
        self._dark_guard = False

        self.treeDarks.expandAll()
        self.dark_catalog = catalog
        rootItem = self.modelDarks.item(0,0)
        if rootItem:
            for i in range(rootItem.rowCount()):
                self._update_parent_tristate(rootItem.child(i,0))
        self.log(f"DARKS: with_exp={with_exp}, files_indexed={total}")

    def _set_all_dirs(self, checked: bool):
        state = Qt.Checked if checked else Qt.Unchecked
        rootItem = self.modelFlats.item(0,0)
        if not rootItem: return
        for i in range(rootItem.rowCount()):
            dirItem = rootItem.child(i,0)
            if dirItem.isCheckable():
                dirItem.setCheckState(state)

    def _set_all_darks(self, checked: bool):
        state = Qt.Checked if checked else Qt.Unchecked
        rootItem = self.modelDarks.item(0,0)
        if not rootItem: return
        self._dark_guard = True
        for i in range(rootItem.rowCount()):
            typItem = rootItem.child(i,0)
            if typItem and typItem.isCheckable():
                typItem.setCheckState(state)
                self._set_children_check(typItem, state)
        self._dark_guard = False
        for i in range(rootItem.rowCount()):
            self._update_parent_tristate(rootItem.child(i,0))

    # ----- tri-state helpers -----
    def _set_children_check(self, item: QStandardItem, state: Qt.CheckState):
        eff_state = Qt.Checked if state == Qt.PartiallyChecked else state
        for i in range(item.rowCount()):
            ch = item.child(i, 0)
            if ch and ch.isCheckable():
                ch.setCheckState(eff_state)
            if ch:
                self._set_children_check(ch, eff_state)

    def _update_parent_tristate(self, item: QStandardItem):
        parent = item.parent()
        while parent:
            total = 0
            checked = 0
            partial = False
            for i in range(parent.rowCount()):
                ch = parent.child(i, 0)
                if not ch or not ch.isCheckable():
                    continue
                total += 1
                var_st = ch.checkState()
                if var_st == Qt.PartiallyChecked:
                    partial = True
                elif var_st == Qt.Checked:
                    checked += 1

            self._dark_guard = True
            try:
                if partial or (0 < checked < total):
                    parent.setCheckState(Qt.PartiallyChecked)
                elif checked == total and total > 0:
                    parent.setCheckState(Qt.Checked)
                else:
                    parent.setCheckState(Qt.Unchecked)
            finally:
                self._dark_guard = False

            parent = parent.parent()

    def _dark_item_changed(self, item: QStandardItem):
        if self._dark_guard or not item or not item.isCheckable():
            return
        self._dark_guard = True
        try:
            st = item.checkState()
            eff_state = Qt.Checked if st == Qt.PartiallyChecked else st
            if item.rowCount() > 0:
                self._set_children_check(item, eff_state)
        finally:
            self._dark_guard = False
        self._update_parent_tristate(item)

    def _gather_allowed_darks(self):
        allowed = set()
        rootItem = self.modelDarks.item(0,0)
        if not rootItem: return []
        for i in range(rootItem.rowCount()):
            typItem = rootItem.child(i,0)
            for j in range(typItem.rowCount()):
                expItem = typItem.child(j,0)
                for k in range(expItem.rowCount()):
                    fItem = expItem.child(k,0)
                    if fItem.isCheckable() and fItem.checkState()==Qt.Checked:
                        allowed.add(fItem.text())
        return [d for d in self.dark_catalog if d["path"] in allowed]

    def _gather_selected_plan(self):
        selected = []
        rootItem = self.modelFlats.item(0,0)
        if not rootItem: return []
        for i in range(rootItem.rowCount()):
            dirItem = rootItem.child(i,0)
            if dirItem.isCheckable() and dirItem.checkState()==Qt.Checked:
                dirPath = dirItem.text()
                groups = []
                for j in range(dirItem.rowCount()):
                    groupNode = dirItem.child(j,0)
                    label = groupNode.text()
                    try:
                        ex_key = label.split("s",1)[0]
                        exposure = float(ex_key)
                    except Exception:
                        continue
                    files = []
                    for k in range(groupNode.rowCount()):
                        files.append(os.path.join(dirPath, groupNode.child(k,0).text()))
                    # (By construction, groups in the UI already have >=3 files)
                    groups.append({"exposure": exposure, "files": files, "want": {}})
                selected.append({"dirPath": dirPath, "groups": groups})
        return selected

    # ---------- run PixInsight ----------
    def runSelected(self):
        pi_exe = self.piExeEdit.text().strip()
        if not pi_exe or not Path(pi_exe).exists():
            QMessageBox.warning(self, "Missing PixInsight", "Select a valid PixInsight.exe.")
            return

        selected_plan = self._gather_selected_plan()
        if not selected_plan:
            QMessageBox.information(self, "Nothing selected", "Tick at least one directory in the flat list.")
            return
        allowed_darks = self._gather_allowed_darks()
        if not allowed_darks:
            QMessageBox.information(self, "No darks selected", "Add a Dark root and select eligible dark(s) in the Dark Inventory.")
            return

        # --- Mirror outputs under <BaseName>_processed placed NEXT TO the selected base ---
        base_roots = [str(Path(self.baseList.item(i).text()).resolve())
                      for i in range(self.baseList.count())]

        def _normsep(p: str) -> str:
            return str(Path(p)).replace("\\", "/")

        def _find_base_root(dir_path: str) -> str | None:
            d = os.path.normcase(os.path.normpath(dir_path))
            cands = []
            for br in base_roots:
                brn = os.path.normcase(os.path.normpath(br))
                if d == brn or d.startswith(brn + os.sep):
                    cands.append(br)
            return max(cands, key=lambda x: len(os.path.normpath(x))) if cands else None

        def _processed_sibling_of(base: str) -> str:
            bp = Path(base)
            try:
                bp = bp.resolve()
            except Exception:
                bp = Path(os.path.normpath(base))
            return str(bp.with_name(bp.name + "_processed"))

        for job in selected_plan:
            d = job["dirPath"]
            base = _find_base_root(d)
            if base:
                out_root = _processed_sibling_of(base)
                rel_dir  = os.path.relpath(d, base)
            else:
                out_root = _processed_sibling_of(d)
                rel_dir  = "."

            if rel_dir == ".":
                rel_dir = ""
            job["outRoot"] = _normsep(out_root)
            job["relDir"]  = rel_dir.replace("\\", "/")

            try:
                os.makedirs(out_root, exist_ok=True)
            except Exception as e:
                self.log(f"[mkdir] failed to create {out_root}: {e}")

            self.log(f"[map] BASE={base or '(none)'}  DIR={d}  ->  OUT_ROOT={job['outRoot']}  REL={job['relDir']}")
        # --- end mapping ---

        def _kexp_py(x: float) -> str:
            s = f"{round(x, 3):.3f}"
            return s.rstrip("0").rstrip(".")

        tdir = Path(tempfile.gettempdir())
        js_path = tdir / "flat_executor_pi.js"
        sentinel_path = tdir / "flat_executor_pi.sentinel.txt"
        try:
            if sentinel_path.exists():
                sentinel_path.unlink()
        except Exception:
            pass

        cfg = {
            "plan": selected_plan,
            "darkCatalog": allowed_darks,
            "match": { "enforceBinning": True, "preferSameGainOffset": True, "preferClosestTemp": True, "maxTempDeltaC": 5.0 },
            "allowNearestExposureWithOptimize": True,
            "cacheDirName": "_DarkMasters",
            "calibratedSubdirBase": "_CalibratedFlats",
            # masterSubdirName kept in config for back-compat, but not used by PJSR when mirroring:
            "masterSubdirName": self.masterSubdirEdit.text().strip() or "Masters",
            "xisfHintsCal": "",
            "xisfHintsMaster": "compression-codec zlib+sh; compression-level 9; checksum sha1",
            "rejection": { "lowSigma": 5.0, "highSigma": 5.0 },
            "sentinelPath": str(sentinel_path).replace("\\", "/"),
            "deleteCalibrated": bool(self.delCalibChk.isChecked())
        }

        js_text = PJSR_TEMPLATE.replace(
            "%CONFIG_JSON_HERE%", '"' + json.dumps(cfg).replace("\\", "\\\\").replace('"','\\"') + '"'
        )

        with open(js_path, "w", encoding="utf-8") as f:
            f.write(js_text)

        self.log(f"Generated PJSR: {js_path}")
        # Show where results will land for clarity
        if selected_plan:
            self.log("[Output mapping]")
            for job in selected_plan:
                self.log(f"  {job['dirPath']}  ->  {job.get('outRoot','?')}{('/'+job.get('relDir','')) if job.get('relDir') else ''}")

        # Use --run to avoid startup-script signature requirement.
        args_variants = [
            [pi_exe, f'--run={str(js_path)}', '--force-exit'],
            [pi_exe, '--run', str(js_path), '--force-exit'],
        ]

        def worker():
            for i, args in enumerate(args_variants, 1):
                self.log(f"[PixInsight attempt {i}/{len(args_variants)}] args={args}")
                try:
                    proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
                    for line in proc.stdout:
                        self.log(line.rstrip("\n"))
                    code = proc.wait()
                    self.log(f"[PI exit code={code}]")
                except Exception as e:
                    self.log(f"[spawn error] {e}")
                    continue

                if sentinel_path.exists():
                    msg = sentinel_path.read_text(encoding="utf-8", errors="ignore").strip()
                    self.log(f"[sentinel] {msg}")
                    break
                else:
                    self.log("[sentinel] missing; trying next variant...")

            # CHANGE: tidy up logfile handle when worker completes
            try:
                if self._log_fh:
                    self._log_fh.flush()
            except Exception:
                pass

        threading.Thread(target=worker, daemon=True).start()

# ---- main ----
if __name__ == "__main__":
    app = QApplication(sys.argv)
    w = FlatMasterApp()
    w.show()
    sys.exit(app.exec())
