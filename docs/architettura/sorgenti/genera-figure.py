# Genera le tre figure prodotte per questo documento (le altre quattro
# provengono dalla versione precedente e non hanno sorgente qui).
# Uso:  cd docs/architettura/sorgenti && python3 genera-figure.py
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch

BORDER = "#3d4a5c"; TEXT = "#1a1a1a"
GREEN="#e3f2e1"; BLUE="#dbeafe"; PURPLE="#ece4f5"; RED="#fde2e2"; YELLOW="#fdf3d8"; GREY="#eef2f6"; ORANGE="#fde8d5"

def fig(w,h):
    f,ax = plt.subplots(figsize=(w,h), dpi=150)
    ax.set_xlim(0,100); ax.set_ylim(0,100); ax.axis("off")
    return f,ax

def box(ax,x,y,w,h,label,fill=GREY,bold=True,fs=10,sub=None):
    ax.add_patch(FancyBboxPatch((x,y),w,h, boxstyle="round,pad=0,rounding_size=1.4",
                 fc=fill, ec=BORDER, lw=1.4))
    if sub:
        ax.text(x+w/2, y+h*0.62, label, ha="center", va="center", fontsize=fs,
                fontweight="bold" if bold else "normal", color=TEXT)
        ax.text(x+w/2, y+h*0.28, sub, ha="center", va="center", fontsize=fs-2.2, color="#44506b")
    else:
        ax.text(x+w/2, y+h/2, label, ha="center", va="center", fontsize=fs,
                fontweight="bold" if bold else "normal", color=TEXT)

def arrow(ax,x1,y1,x2,y2,style="-|>",dashed=False,lw=1.4,color=BORDER):
    ax.add_patch(FancyArrowPatch((x1,y1),(x2,y2), arrowstyle=style, mutation_scale=13,
                 lw=lw, color=color, linestyle="--" if dashed else "-",
                 shrinkA=1, shrinkB=1, connectionstyle="arc3,rad=0"))

def title(ax,t,sub=None):
    ax.text(50,96,t,ha="center",va="center",fontsize=16,fontweight="bold",color=TEXT)
    if sub: ax.text(50,89.5,sub,ha="center",va="center",fontsize=10.5,color="#44506b")

# ---------------------------------------------------------------- Fig 2
f,ax = fig(11.4,5.8)
title(ax,"Ciclo di vita di un run","La fase corrente è il checkpoint: un errore non la fa avanzare")
fasi = ["Created","Selecting","Expanding","Validating","Planning","Executing","Completed"]
colori = [BLUE,GREEN,GREEN,GREEN,GREEN,PURPLE,GREEN]
x=2.0; w=12.6; gap=1.3
xs=[]
for i,(n,c) in enumerate(zip(fasi,colori)):
    box(ax,x,60,w,12.5,n,fill=c,fs=10)
    xs.append(x+w/2)
    if i < len(fasi)-1: arrow(ax,x+w,66.2,x+w+gap,66.2)
    x += w+gap

box(ax,60,36,38,12.5,"CompletedWithErrors",fill=YELLOW,fs=10,sub="concluso, con slice abbandonate da analizzare")
arrow(ax,xs[6],60,84,48.5)

box(ax,4,36,26,12.5,"Failed",fill=RED,fs=10,sub="terminale: non riprende")
arrow(ax,xs[3],60,20,48.5)
ax.text(30.5,54,"validazione fallita\no difetto",ha="center",fontsize=8.2,color="#44506b")

box(ax,33,36,24,12.5,"Aborted",fill=ORANGE,fs=10,sub="annullato")
arrow(ax,xs[5],60,47,48.5)

ax.text(50,23.5,"Interruzione (guasto di rete, timeout, finestra chiusa): la fase NON cambia, InterruptionCount +1,\n"
              "il run resta riprendibile dal checkpoint. Oltre MaxRunInterruptions (5) diventa Failed.",
        ha="center",va="center",fontsize=9.2,color="#44506b")
ax.text(50,8,"Il set dei candidati è congelato alla prima selezione: una ripresa dopo tre notti opera sullo stesso insieme.",
        ha="center",va="center",fontsize=8.8,style="italic",color="#5a6478")
f.savefig("../figure/fig2_pipeline.png",bbox_inches="tight",facecolor="white"); plt.close(f)

# ---------------------------------------------------------------- Fig bisezione
f,ax = fig(11.4,6.0)
title(ax,"Bisezione di una slice (D-11)","Un aggregato che rifiuta la cancellazione non trascina gli altri")
box(ax,33,68,34,12,"Slice  #0",fill=PURPLE,sub="4 aggregati — DELETE fallisce (Sql547)")
ax.text(69.5,74,"divisibile: si divide",fontsize=8.6,color="#44506b",va="center",ha="left")

box(ax,8,46,32,12,"Slice  #1",fill=GREEN,sub="2 aggregati — committata")
box(ax,58,46,32,12,"Slice  #2",fill=PURPLE,sub="2 aggregati — fallisce ancora")
arrow(ax,42,68,28,58); arrow(ax,58,68,72,58)

box(ax,50,24,18,12,"Slice  #3",fill=GREEN,sub="1 — committata")
box(ax,72,24,18,12,"Slice  #4",fill=RED,sub="1 — abbandonata")
arrow(ax,68,46,62,36); arrow(ax,78,46,80,36)

ax.text(50,14,"Tre aggregati su quattro vengono cancellati; l'abbandono riguarda solo il colpevole.\n"
              "Con l'abbandono in blocco se ne sarebbero persi quattro. Costo: ~2·log2(N) transazioni invece di N.",
        ha="center",va="center",fontsize=9.2,color="#44506b")
ax.text(50,3,"Un Collective non si divide mai: è l'unità atomica. ParentBatchNo conserva la genealogia, SplitDepth è il freno (MaxSplitDepth).",
        ha="center",va="center",fontsize=8.6,style="italic",color="#5a6478")
f.savefig("../figure/fig_bisezione.png",bbox_inches="tight",facecolor="white"); plt.close(f)

# ---------------------------------------------------------------- Fig sicurezza
f,ax = fig(11.4,6.4)
title(ax,"I cinque livelli di sicurezza","Indipendenti: ciascuno ferma la cancellazione da solo")
livelli = [
 ("1  SchemaVerifier", "all'avvio: se lo schema Purge non è allineato, il processo non parte", BLUE),
 ("2  Modalità obbligatoria da riga di comando", "--dry-run o --delete: mai dalla configurazione", GREEN),
 ("3  Dry-run per costruzione", "il servizio simula; l'utenza non ha DELETE su PaymentOrder", GREEN),
 ("4  Gate di approvazione", "impronta della policy approvata e persistita: purge approve", YELLOW),
 ("5  Foreign key NO ACTION", "mai disabilitate: rete finale contro l'incoerenza", RED),
]
y=72
for t,s,c in livelli:
    box(ax,8,y,84,11.5,t,fill=c,fs=10.5,sub=s)
    y-=14
ax.text(50,7.5,"Cancellazione effettiva solo se tutti e cinque lo consentono. L'audit registra ciò che è stato committato, non ciò che è stato tentato.",
        ha="center",va="center",fontsize=9,style="italic",color="#5a6478")
f.savefig("../figure/fig_sicurezza.png",bbox_inches="tight",facecolor="white"); plt.close(f)
print("ok")
