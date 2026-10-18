"""Cleans the bird clips (applied once, in 0.21.2, to every clip but the mouse's; run on its own output it would clean
twice): a spectral-subtraction denoise (noise profile from the clip's quietest frames), a soft gate so the gaps are
silent, trimmed silence, and a level near vanilla's creature sounds (calls -28 dB RMS while sounding, hurt
and death -24, peaks under -6). Needs ffmpeg and numpy. Usage: python tools/clean_sounds.py SRC_SOUNDS_DIR OUT_DIR"""
import sys, subprocess, numpy as np, glob, os
SR=44100; N=2048; H=512
def load(p):
    raw=subprocess.run(["ffmpeg","-v","error","-i",p,"-ac","1","-ar",str(SR),"-f","f32le","-"],capture_output=True).stdout
    return np.frombuffer(raw,dtype=np.float32).astype(np.float64)
def save(p,x):
    os.makedirs(os.path.dirname(p),exist_ok=True)
    subprocess.run(["ffmpeg","-v","error","-y","-f","f32le","-ar",str(SR),"-ac","1","-i","-","-c:a","libvorbis","-q:a","5",p],
                   input=x.astype(np.float32).tobytes(),check=True)
def stft(x):
    w=np.hanning(N); xp=np.pad(x,(N,N)); idx=range(0,len(xp)-N,H)
    return np.array([np.fft.rfft(xp[i:i+N]*w) for i in idx]), len(x)
def istft(X,n):
    w=np.hanning(N); out=np.zeros((len(X)-1)*H+N); norm=np.zeros_like(out)
    for k,f in enumerate(X):
        out[k*H:k*H+N]+=np.fft.irfft(f,N)*w; norm[k*H:k*H+N]+=w*w
    out/=np.maximum(norm,1e-8); return out[N:N+n]
def smooth(a,kt,kf):
    from numpy.lib.stride_tricks import sliding_window_view as sw
    a=np.pad(a,((kt//2,kt//2),(kf//2,kf//2)),mode="edge")
    return sw(a,(kt,kf)).mean(axis=(-1,-2))
def db(v): return 20*np.log10(max(v,1e-12))
def clean(x, target_db, hp):
    # high-pass (as before) by zeroing bins under hp, gently
    X,n=stft(x); mag=np.abs(X); P=mag**2
    fr=P.sum(1); quiet=fr<=np.percentile(fr,15)
    noise=np.minimum(P[quiet].mean(0), np.percentile(P,20,axis=0))   # dense clips: the low percentile bounds it
    G=np.sqrt(np.maximum(1-2.0*noise/np.maximum(P,1e-20), 0.1**2))   # 2x over-subtraction, -20 dB floor
    G=smooth(G,3,5)
    f=np.fft.rfftfreq(N,1/SR); G*=np.clip((f-hp*0.7)/(hp*0.3),0,1)[None,:]
    y=istft(X*G,n)
    # soft gate on the 10 ms envelope: gaps 25 dB down
    fl=441; m=len(y)//fl; env=np.sqrt(np.convolve(y[:m*fl]**2,np.ones(fl*3)/(fl*3),mode="same").reshape(m,fl).mean(1))
    peak=env.max(); thr=peak*10**(-30/20)
    g=np.where(env>thr,1.0,10**(-25/20))
    # release 80 ms, attack 10 ms
    out=np.empty_like(g); c=g[0]
    for i,v in enumerate(g): c = v if v>c else c+(v-c)*0.12; out[i]=c
    gs=np.interp(np.arange(len(y)),np.arange(m)*fl+fl/2,out); y=y*gs
    # trim: from 20 ms before the first loud frame to 60 ms after the last
    loud=np.where(env>peak*10**(-36/20))[0]
    a=max(0,loud[0]*fl-882); b=min(len(y),(loud[-1]+1)*fl+2646); y=y[a:b]
    fi=min(220,len(y)//4); fo=min(1323,len(y)//3)
    y[:fi]*=np.linspace(0,1,fi); y[-fo:]*=np.linspace(1,0,fo)
    # level: RMS of the active (gated-open) part to target_db, peak no higher than -6 dB
    act=y[np.abs(y)>0]; m2=len(y)//fl; e2=np.sqrt((y[:m2*fl].reshape(m2,fl)**2).mean(1)); on=e2>e2.max()*10**(-20/20)
    rms_on=np.sqrt((y[:m2*fl].reshape(m2,fl)[on]**2).mean())
    y*=10**(target_db/20)/rms_on
    pk=np.abs(y).max()
    if pk>10**(-6/20): y*=10**(-6/20)/pk
    return y
HP={"crane":120,"goose":120,"wings":120,"duck":150,"heron":150,"eagle":400,"gull":400,"crow":500,"owl":300}
src,dst=sys.argv[1],sys.argv[2]
for p in sorted(glob.glob(os.path.join(src,"**","*.ogg"),recursive=True)):
    rel=os.path.relpath(p,src); sp=rel.split(os.sep)[0]
    if sp=="mouse": continue   # synthesized, already clean
    name=os.path.basename(rel)
    tgt=-24 if name.startswith(("hurt","death")) else -28
    y=clean(load(p),tgt,HP.get(sp,120))
    save(os.path.join(dst,rel),y)
