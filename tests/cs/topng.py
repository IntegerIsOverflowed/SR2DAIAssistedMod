import sys; from PIL import Image
w,h=int(sys.argv[2]),int(sys.argv[3]); Image.frombytes("RGBA",(w,h),open(sys.argv[1],"rb").read()).save(sys.argv[4]); print("wrote",sys.argv[4])
