#!/bin/bash
cd bsp
targets=`ls -1`
cd ..
for target in $targets
do
	make PROGRAM=hello TARGET=$target CONFIGURATION=debug clean
	make PROGRAM=hello TARGET=$target CONFIGURATION=debug software > $target.log 2>&1
done