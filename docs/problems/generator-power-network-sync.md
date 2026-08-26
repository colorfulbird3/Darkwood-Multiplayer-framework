# Generator + Power Network（v0.9.2）

## 状态

PLANNED / NOT IMPLEMENTED IN 0.8.9.2 — PLANNED — Phase 1 物品链闭环之后再单独 commit

## 问题

0.8.9.1 Generator 只同步 isOn/fuel/lowPower，没有 restorePower/cutPower 的级联 → 灯不同步。

## 修复

- Generator Host：执行原版 turnOn/turnOff → BroadcastStateNow → CaptureStateBundle（Generator + 所有受影响 powerItems）
- Client Apply：拆分 visual-only（fuel 不 drain、ApplyGeneratorVisualState 不触发 onUpdateTime）

## 真机验收

TEST H：Generator
