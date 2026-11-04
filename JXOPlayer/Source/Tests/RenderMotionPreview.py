"""Render the recorded numerical output; no user image or source script is edited."""
import csv
import json
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

root = Path(__file__).resolve().parents[2]
result = root / "Source" / "Results"
reference = json.loads((result / "tremor-reference-analysis.json").read_text(encoding="utf-8"))["reference"]
with (result / "tremor-samples.csv").open(encoding="utf-8") as file:
    generated = [(float(p["seconds"]), (float(p["position"]) - .2) / .6) for p in csv.DictReader(file)]
scale = 2
img = Image.new("RGB", (1100 * scale, 740 * scale), "#f6f8fb")
draw = ImageDraw.Draw(img)
def font(size):
    return ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", size * scale)
def text(x,y,s,size=17,color="#34435a"):
    draw.text((x*scale,y*scale),s,font=font(size),fill=color)
def line(points,color,width=1):
    draw.line([(int(x*scale),int(y*scale)) for x,y in points],fill=color,width=width*scale)
def plot(title, points, start, end, top, color):
    text(72,top,title,19)
    x0,x1,y0,y1=86,1040,top+38,top+152
    for v in [0,.25,.5,.75,1]:
        y=y1-v*(y1-y0)
        line([(x0,y),(x1,y)],"#dce2ec")
        text(37,y-9,str(int(v*100)),12,"#64748b")
    for i in range(5):
        t=start+(end-start)*i/4; x=x0+(x1-x0)*i/4
        text(x-12,y1+8,f"{t:.2f}s",12,"#64748b")
    p=[(x0+(t-start)/(end-start)*(x1-x0), y1-v*(y1-y0)) for t,v in points if start<=t<=end]
    line(p,color,2)
text(62,26,"痉挛曲线：脚本片段参考与新版输出",27,"#172131")
text(62,71,"连续输出位置 · 直线折返 · 幅度与间隔不均匀",16)
plot("脚本库后半段参考（局部片段；原脚本位置 %）",reference,0,reference[-1][0],115,"#59657c")
plot("新版痉挛（输出取样换算为配置范围内位置 %）",generated,0,6,310,"#7457ab")
plot("新版局部放大：每次折返的高低、间隔均有变化",generated,1,2,505,"#267d71")
text(72,702,"参考分析：220 份主脚本 / 115 个匹配窗口。下两图来自新版运动引擎的 10 ms 测试取样。",13)
img.resize((1100,740),Image.Resampling.LANCZOS).save(root/"Docs"/"痉挛曲线对照.png")
