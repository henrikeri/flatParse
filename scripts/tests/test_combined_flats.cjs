const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const template = fs.readFileSync(path.join(__dirname, '../../src/FlatMaster.Infrastructure/Services/PixInsightTemplate.pjsr'), 'utf8').replace(/\r\n/g,'\n');
const run = template.slice(template.indexOf('function run(){'), template.indexOf('\ntry{\n  if (typeof CFG'));
function execute({missingDark = false, requireDarks = true, partialCalibration = false} = {}) {
  const calibrated = [], integrated = [], receipts = [], cleaned = [];
  const group = {exposure:20, outputTag:'Mixed20-30', files:['a.fit','b.fit','c.fit'], completionPath:'receipt', calibrationGroups:[
    {exposure:20, files:['a.fit'], want:{gain:100}}, {exposure:30, files:['b.fit','c.fit'], want:{gain:100}}
  ]};
  const context = {
    CFG:{plan:[{dirPath:'source/Ha',outRoot:'output',relDir:'Ha',groups:[group]}], requireDarks, deleteCalibrated:true},
    __SENTINEL:'sentinel', ImageIntegration:function(){}, ImageCalibration:function(){}, ImageWindow:{},
    File:{exists:()=>false, remove(){},removeDirectory:p=>cleaned.push(p)},
    log(){},warn(){},pulse(){},ensureDir(){},kexp:String,
    joinPath:(a,b)=>a+'/'+b,guessFilterFrom:()=> 'Ha',guessDateFromPath:()=> 'UNKNOWNDATE',tempOutputPath:p=>p+'.tmp',
    pickDarkFor:(exposure,want)=> { assert.equal(want.gain,100);return missingDark && exposure===30 ? null : {path:'dark'+exposure,kind:'exact'}; },
    calibrateFlats:(files,dir,dark)=> {calibrated.push({files,dark});return partialCalibration && dark==='dark30' ? [] : files.map(f=>dir+'/'+f+'_c');},
    rejectLowSignalCalibratedFlats:files=> {assert.equal(files.length,3); return files;},
    integrateToMaster:(files,out,forDark,hints,rej,ext,type,exposures)=>integrated.push({files,out,exposures}),
    publishOutput(){},touch:(p,s)=>receipts.push({p,s})
  };
  try {vm.runInNewContext(run+'\nrun();',context);return {calibrated,integrated,receipts,cleaned};}
  catch(error) {return {error,calibrated,integrated,receipts,cleaned};}
}
const result=execute();
assert.equal(result.error,undefined);
assert.deepEqual(result.calibrated.map(c=>[c.files.length,c.dark]),[[1,'dark20'],[2,'dark30']]);
assert.equal(result.integrated.length,1);assert.equal(result.integrated[0].files.length,3);
assert.deepEqual(Array.from(result.integrated[0].exposures),[20,30]);
assert.equal(JSON.parse(result.receipts.find(r=>r.p==='receipt').s).integratedFrameCount,3);
assert.equal(result.cleaned.length,2);
for (const options of [{missingDark:true},{partialCalibration:true}]) {
 const failed=execute(options);assert.ok(failed.error);assert.equal(failed.integrated.length,0);assert.equal(failed.receipts.length,0);
}
const optional=execute({missingDark:true,requireDarks:false});
assert.equal(optional.error,undefined);assert.equal(optional.calibrated.length,1);assert.equal(optional.integrated[0].files.length,3);
console.log('PASS: mixed exposure calibration, pooled integration, completion receipt, cleanup, required-dark failure, partial calibration, and optional calibration');
