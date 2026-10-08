# Linux/GCC verification build: differential test + benchmark against the ORIGINAL sources.
CXX      ?= g++
CXXFLAGS ?= -O2 -std=c++17
# also works with:  make CXX=clang++ test
NAT      := ../native
REFSRC   := ref/SR2D.cpp ref/Filter.cpp ref/Transform.cpp ref/Bump.cpp ref/Vector.cpp
REFNAMES := $(shell grep -oE '^  [A-Z_0-9]+' $(NAT)/SR2D.def | tr -d ' ' | grep -v SR2D_)
REFDEFS  := $(foreach n,$(REFNAMES),-D$(n)=REF_$(n))
BUILD    := build

all: $(BUILD)/difftest $(BUILD)/bench $(BUILD)/warptest $(BUILD)/linetest $(BUILD)/polytest $(BUILD)/blurtest $(BUILD)/areatest $(BUILD)/cubictest $(BUILD)/fringetest $(BUILD)/fxtest $(BUILD)/fuzztest $(BUILD)/threadtest $(BUILD)/floodtest $(BUILD)/voxtest $(BUILD)/blendtest $(BUILD)/dispatchtest

$(BUILD):
	mkdir -p $(BUILD)

# -fno-builtin: the original Vector.cpp defines its own (quirky) abs(); clang would
# otherwise treat that name as the builtin and silently change the reference output.
$(BUILD)/ref_%.o: ref/%.cpp | $(BUILD)
	sed -e 's/#include <windows.h>//; s/#include <intrin.h>//; s/#include <malloc.h>//; /#pragma comment/d' $< \
	  | sed -e '/BOOL APIENTRY DllMain/,/^}/d' > $(BUILD)/ref_$*.cpp
	$(CXX) $(CXXFLAGS) -w -fwrapv -fno-operator-names -fno-builtin -include shim/ref_shim.h $(REFDEFS) -c $(BUILD)/ref_$*.cpp -o $@

$(BUILD)/k_sse2.o: $(NAT)/sr2d_kernels_sse2.cpp $(NAT)/sr2d_kernels.inl $(NAT)/sr2d_fx.inl $(NAT)/sr2d_select.inl $(NAT)/sr2d_voxel.inl $(NAT)/sr2d_simd.h $(NAT)/sr2d_api.h | $(BUILD)
	$(CXX) $(CXXFLAGS) -Wall -Wextra -msse2 -c $< -o $@
$(BUILD)/k_avx2.o: $(NAT)/sr2d_kernels_avx2.cpp $(NAT)/sr2d_kernels.inl $(NAT)/sr2d_fx.inl $(NAT)/sr2d_select.inl $(NAT)/sr2d_voxel.inl $(NAT)/sr2d_simd.h $(NAT)/sr2d_api.h | $(BUILD)
	$(CXX) $(CXXFLAGS) -Wall -Wextra -mavx2 -ffp-contract=off -c $< -o $@
$(BUILD)/sr2d.o: $(NAT)/sr2d.cpp $(NAT)/sr2d_ops.h | $(BUILD)
	$(CXX) $(CXXFLAGS) -Wall -Wextra -c $< -o $@

REFOBJ := $(patsubst ref/%.cpp,$(BUILD)/ref_%.o,$(REFSRC))
NEWOBJ := $(BUILD)/k_sse2.o $(BUILD)/k_avx2.o $(BUILD)/sr2d.o

$(BUILD)/difftest: difftest.cpp $(REFOBJ) $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/bench: bench.cpp $(REFOBJ) $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/warptest: warptest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/linetest: linetest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/polytest: polytest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/blurtest: blurtest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/areatest: areatest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/cubictest: cubictest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/fringetest: fringetest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/fxtest: fxtest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/fuzztest: fuzztest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/threadtest: threadtest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm -lpthread
$(BUILD)/voxtest: voxtest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/floodtest: floodtest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/blendtest: blendtest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/regchk: regchk.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm
$(BUILD)/dispatchtest: dispatchtest.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm -lpthread
$(BUILD)/noisesnap: noisesnap.cpp $(NEWOBJ)
	$(CXX) $(CXXFLAGS) -w $^ -o $@ -lm

# sanitizer build of the kernels + fuzzer (slow; ~2.5 min for the default 6000 iterations)
ASANFLAGS = -O2 -std=c++17 -fsanitize=address,undefined -fno-sanitize=alignment
asan: fuzztest.cpp
	$(CXX) $(ASANFLAGS) -msse2 -c ../native/sr2d_kernels_sse2.cpp -o $(BUILD)/asan_sse2.o
	$(CXX) $(ASANFLAGS) -mavx2 -ffp-contract=off -c ../native/sr2d_kernels_avx2.cpp -o $(BUILD)/asan_avx2.o
	$(CXX) $(ASANFLAGS) -c ../native/sr2d.cpp -o $(BUILD)/asan_sr2d.o
	$(CXX) -O1 -std=c++17 -w -fsanitize=address,undefined -fno-sanitize=alignment fuzztest.cpp $(BUILD)/asan_sse2.o $(BUILD)/asan_avx2.o $(BUILD)/asan_sr2d.o -o $(BUILD)/fuzztest_asan -lm
	ASAN_OPTIONS=detect_leaks=0 ./$(BUILD)/fuzztest_asan 6000
	$(CXX) -O1 -std=c++17 -w -fsanitize=address,undefined -fno-sanitize=alignment threadtest.cpp $(BUILD)/asan_sse2.o $(BUILD)/asan_avx2.o $(BUILD)/asan_sr2d.o -o $(BUILD)/threadtest_asan -lm -lpthread
	ASAN_OPTIONS=detect_leaks=0 ./$(BUILD)/threadtest_asan 10
	$(CXX) -O1 -std=c++17 -w -fsanitize=address,undefined -fno-sanitize=alignment blendtest.cpp $(BUILD)/asan_sse2.o $(BUILD)/asan_avx2.o $(BUILD)/asan_sr2d.o -o $(BUILD)/blendtest_asan -lm
	ASAN_OPTIONS=detect_leaks=0 ./$(BUILD)/blendtest_asan 20
	$(CXX) -O1 -std=c++17 -w -fsanitize=address,undefined -fno-sanitize=alignment linetest.cpp $(BUILD)/asan_sse2.o $(BUILD)/asan_avx2.o $(BUILD)/asan_sr2d.o -o $(BUILD)/linetest_asan -lm
	ASAN_OPTIONS=detect_leaks=0 ./$(BUILD)/linetest_asan 500
	$(CXX) -O1 -std=c++17 -w -fsanitize=address,undefined -fno-sanitize=alignment regchk.cpp $(BUILD)/asan_sse2.o $(BUILD)/asan_avx2.o $(BUILD)/asan_sr2d.o -o $(BUILD)/regchk_asan -lm
	ASAN_OPTIONS=detect_leaks=0 ./$(BUILD)/regchk_asan

# ThreadSanitizer build of the kernels + thread test (data races in the scratch slot / kernels)
TSANFLAGS = -O1 -std=c++17 -fsanitize=thread
tsan: threadtest.cpp
	$(CXX) $(TSANFLAGS) -msse2 -c ../native/sr2d_kernels_sse2.cpp -o $(BUILD)/tsan_sse2.o
	$(CXX) $(TSANFLAGS) -mavx2 -ffp-contract=off -c ../native/sr2d_kernels_avx2.cpp -o $(BUILD)/tsan_avx2.o
	$(CXX) $(TSANFLAGS) -c ../native/sr2d.cpp -o $(BUILD)/tsan_sr2d.o
	$(CXX) $(TSANFLAGS) -w threadtest.cpp $(BUILD)/tsan_sse2.o $(BUILD)/tsan_avx2.o $(BUILD)/tsan_sr2d.o -o $(BUILD)/threadtest_tsan -lm -lpthread
	./$(BUILD)/threadtest_tsan 6
	$(CXX) $(TSANFLAGS) -w dispatchtest.cpp $(BUILD)/tsan_sse2.o $(BUILD)/tsan_avx2.o $(BUILD)/tsan_sr2d.o -o $(BUILD)/dispatchtest_tsan -lm -lpthread
	./$(BUILD)/dispatchtest_tsan 4

# Windows SDK headers (rpcndr.h via windows.h) #define small, hyper, near, far, pascal ... as
# plain macros. The Linux build never sees them, so compile every TU once with the same macros
# defined to catch identifiers that would break the MSVC / clang-cl build (the "small" incident).
WINMACROS := -Dsmall=char -Dhyper="long long" -Dnear= -Dfar= -Dpascal= -Dcdecl= -DIN= -DOUT= -DOPTIONAL=
wincheck:
	$(CXX) $(CXXFLAGS) -w -msse2 $(WINMACROS) -fsyntax-only $(NAT)/sr2d_kernels_sse2.cpp
	$(CXX) $(CXXFLAGS) -w -mavx2 $(WINMACROS) -fsyntax-only $(NAT)/sr2d_kernels_avx2.cpp
	$(CXX) $(CXXFLAGS) -w $(WINMACROS) -fsyntax-only $(NAT)/sr2d.cpp
	@echo "wincheck: no clashes with Windows SDK macros"
	python3 cs/projchk.py

test: wincheck $(BUILD)/difftest $(BUILD)/warptest $(BUILD)/linetest $(BUILD)/polytest $(BUILD)/blurtest $(BUILD)/areatest $(BUILD)/cubictest $(BUILD)/fringetest $(BUILD)/fxtest $(BUILD)/fuzztest $(BUILD)/threadtest $(BUILD)/floodtest $(BUILD)/voxtest $(BUILD)/blendtest $(BUILD)/dispatchtest $(BUILD)/regchk
	./$(BUILD)/difftest 3000
	./$(BUILD)/warptest 600
	./$(BUILD)/linetest 4000
	./$(BUILD)/polytest 1000
	./$(BUILD)/blurtest 300
	./$(BUILD)/areatest
	./$(BUILD)/cubictest 600
	./$(BUILD)/fringetest
	./$(BUILD)/fxtest 200
	./$(BUILD)/fuzztest 1500
	./$(BUILD)/threadtest 6
	./$(BUILD)/floodtest 1500
	./$(BUILD)/blendtest 200
	./$(BUILD)/regchk
	./$(BUILD)/dispatchtest 4
	./$(BUILD)/voxtest 200 0
bench: $(BUILD)/bench
	./$(BUILD)/bench
clean:
	rm -rf $(BUILD)
.PHONY: all test bench clean so wincheck

# Linux shared library of the kernels (for the C# headless checks in tests/cs, which P/Invoke "SR2D64")
so: build/libSR2D64.so
build/libSR2D64.so: ../native/sr2d_kernels_sse2.cpp ../native/sr2d_kernels_avx2.cpp ../native/sr2d.cpp ../native/sr2d_kernels.inl ../native/sr2d_fx.inl ../native/sr2d_select.inl ../native/sr2d_voxel.inl
	@mkdir -p build
	$(CXX) -O2 -std=c++17 -fPIC -msse2 -c ../native/sr2d_kernels_sse2.cpp -o build/so_sse2.o
	$(CXX) -O2 -std=c++17 -fPIC -mavx2 -ffp-contract=off -c ../native/sr2d_kernels_avx2.cpp -o build/so_avx2.o
	$(CXX) -O2 -std=c++17 -fPIC -c ../native/sr2d.cpp -o build/so_main.o
	$(CXX) -shared build/so_sse2.o build/so_avx2.o build/so_main.o -o $@
