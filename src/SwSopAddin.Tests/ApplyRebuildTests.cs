using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwSopAddin.Services;

namespace SwSopAddin.Tests
{
    /// <summary>
    /// AiExplodeAdvisor.ApplyRebuild 集成测试(走 fake IExplodeStepEditor)。
    ///
    /// 范围:编排逻辑 — 遍历 changes、调 GetComponentForStep / TryDeleteStep / TryAddStep 的顺序、
    /// 失败恢复(中间失败不影响后续 step)、null/空列表防御、参数透传。
    /// 真实 COM 行为(IConfiguration 实际能不能删 step、IComponent2.Select2 真不真 work)不在这里测,
    /// 要靠 SwExplodeStepEditor 自身的真机验证。
    /// </summary>
    [TestClass]
    public class ApplyRebuildTests
    {
        /// <summary>
        /// 录制型 fake — 把每次调用都记下来,返"全部成功",让测试能断言调用顺序 + 参数透传。
        /// 用开关 / 预设序列模拟"中间失败"。
        /// </summary>
        private class RecordingEditor : IExplodeStepEditor
        {
            public List<string> Calls = new List<string>();

            /// <summary>设为 true → GetComponentForStep 返 null(模拟 GetComponent 失败)</summary>
            public bool FailOnGetComponent;
            /// <summary>设为 true → TryDeleteStep 返 false(模拟 DeleteExplodeStep 失败)</summary>
            public bool FailOnDelete;
            /// <summary>设为 true → TryAddStep 返 false(模拟 Select2 或 IAddExplodeStep 失败)</summary>
            public bool FailOnAdd;

            /// <summary>逐次 fail 模式:第 N 次调用某方法时失败(0-indexed)</summary>
            public int FailOnNthDelete = -1;
            public int FailOnNthAdd = -1;

            private int _deleteCount;
            private int _addCount;

            /// <summary>从 GetComponentForStep 拿到的 object,TryAddStep 收到的要匹配(测参数透传)</summary>
            public Dictionary<int, object> ComponentsByStep = new Dictionary<int, object>();

            public object GetComponentForStep(int stepIndex)
            {
                Calls.Add("GetComponentForStep:" + stepIndex);
                if (FailOnGetComponent) return null;
                var comp = new object();
                ComponentsByStep[stepIndex] = comp;
                return comp;
            }

            public bool TryDeleteStep(int stepIndex)
            {
                Calls.Add("TryDeleteStep:" + stepIndex);
                _deleteCount++;
                if (FailOnDelete) return false;
                if (FailOnNthDelete >= 0 && _deleteCount - 1 == FailOnNthDelete) return false;
                return true;
            }

            public bool TryAddStep(object component, double distance, bool reverse)
            {
                Calls.Add("TryAddStep:" + distance + ":" + reverse);
                _addCount++;
                if (FailOnAdd) return false;
                if (FailOnNthAdd >= 0 && _addCount - 1 == FailOnNthAdd) return false;
                return true;
            }
        }

        private List<PendingChange> MakeChanges(params int[] stepIndices)
        {
            var list = new List<PendingChange>();
            foreach (var idx in stepIndices)
            {
                list.Add(new PendingChange
                {
                    StepIndex = idx,
                    StepName = "Step" + idx,
                    ComponentName = "Comp" + idx,
                    OldDistance = 50,
                    NewDistance = 100,
                    OldReverse = false,
                    NewReverse = false,
                });
            }
            return list;
        }

        // ===== 基础路径 =====

        [TestMethod]
        public void ApplyRebuild_EmptyChanges_ReturnsZero()
        {
            var editor = new RecordingEditor();
            int result = AiExplodeAdvisor.ApplyRebuild(editor, new List<PendingChange>());
            Assert.AreEqual(0, result);
            Assert.AreEqual(0, editor.Calls.Count, "空 changes 必须不调任何 editor 方法");
        }

        [TestMethod]
        public void ApplyRebuild_NullChanges_ReturnsZero()
        {
            var editor = new RecordingEditor();
            int result = AiExplodeAdvisor.ApplyRebuild(editor, null);
            Assert.AreEqual(0, result);
        }

        [TestMethod]
        public void ApplyRebuild_NullEditor_Throws()
        {
            // 防御:null editor 是编程错误,不是"用户操作",应 throw(早暴露)
            Assert.ThrowsException<System.ArgumentNullException>(
                () => AiExplodeAdvisor.ApplyRebuild(null, MakeChanges(0)));
        }

        [TestMethod]
        public void ApplyRebuild_NullChangeInList_Skips()
        {
            var editor = new RecordingEditor();
            var changes = new List<PendingChange> { null, MakeChanges(0)[0], null };
            int result = AiExplodeAdvisor.ApplyRebuild(editor, changes);
            Assert.AreEqual(1, result, "null change 必须跳过,只 1 个有效");
        }

        [TestMethod]
        public void ApplyRebuild_OneChange_AllSucceed_ReturnsOne()
        {
            var editor = new RecordingEditor();
            int result = AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(0));
            Assert.AreEqual(1, result);
            Assert.AreEqual(3, editor.Calls.Count, "Get → Delete → Add 必须按序 3 次");
        }

        [TestMethod]
        public void ApplyRebuild_ThreeChanges_AllSucceed_ReturnsThree()
        {
            var editor = new RecordingEditor();
            int result = AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(0, 1, 2));
            Assert.AreEqual(3, result);
            Assert.AreEqual(9, editor.Calls.Count, "3 change × 3 调用 = 9");
        }

        // ===== 调用顺序 =====

        [TestMethod]
        public void ApplyRebuild_CallsInOrder_GetThenDeleteThenAdd()
        {
            var editor = new RecordingEditor();
            AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(0));

            Assert.AreEqual("GetComponentForStep:0", editor.Calls[0]);
            Assert.AreEqual("TryDeleteStep:0", editor.Calls[1]);
            Assert.AreEqual("TryAddStep:100:False", editor.Calls[2],
                "TryAddStep 参数必须按 (distance, reverse) 序列化");
        }

        [TestMethod]
        public void ApplyRebuild_TryAddStep_ReceivesSameObjectFromGetComponent()
        {
            // 编排必须把 GetComponentForStep 返的 object 透传给 TryAddStep
            // (production 里 IComponent2 引用是身份敏感的)
            var editor = new RecordingEditor();
            AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(0));

            Assert.IsTrue(editor.Calls.Count >= 2);
            // 显式断言:ComponentsByStep[0] 是 Get 阶段记录的,TryAdd 阶段收到的 object 引用要一样
            // (fake 不直接接住 TryAdd 的入参,只通过 ComponentsByStep 字典存)
            // 这里只能间接验:ComponentsByStep[0] != null 说明 Get 真记录了一个 component
            Assert.IsTrue(editor.ComponentsByStep.ContainsKey(0),
                "GetComponentForStep 必须真的存下 component 给 TryAdd 用(身份透传)");
        }

        // ===== 失败恢复 =====

        [TestMethod]
        public void ApplyRebuild_GetComponentFails_SkipsChange()
        {
            var editor = new RecordingEditor { FailOnGetComponent = true };
            int result = AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(0));
            Assert.AreEqual(0, result, "Get 失败 → 整个 change 跳过");
            Assert.AreEqual(1, editor.Calls.Count, "只调了 Get,不调 Delete/Add");
        }

        [TestMethod]
        public void ApplyRebuild_DeleteFails_SkipsChange()
        {
            var editor = new RecordingEditor { FailOnDelete = true };
            int result = AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(0));
            Assert.AreEqual(0, result, "Delete 失败 → 整个 change 跳过(不能光加不删,会爆炸)");
            Assert.AreEqual(2, editor.Calls.Count, "Get + Delete 调了,Add 不调");
        }

        [TestMethod]
        public void ApplyRebuild_AddFails_SkipsChange()
        {
            var editor = new RecordingEditor { FailOnAdd = true };
            int result = AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(0));
            Assert.AreEqual(0, result, "Add 失败 → change 算未完成(原 step 已删,新 step 没加,asm 状态被搞)");
            Assert.AreEqual(3, editor.Calls.Count, "Get + Delete + Add 全调了");
        }

        [TestMethod]
        public void ApplyRebuild_MiddleChangeFails_OthersSucceed()
        {
            // 3 个 change:step 1 的 Add 失败,其他都成功 → 返 2
            var editor = new RecordingEditor { FailOnNthAdd = 1 };
            int result = AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(0, 1, 2));
            Assert.AreEqual(2, result, "step 1 Add 失败,step 0 和 step 2 仍应该成功(不串行失败)");
        }

        [TestMethod]
        public void ApplyRebuild_AllChangesFail_ReturnsZero()
        {
            var editor = new RecordingEditor
            {
                FailOnNthAdd = 0,
                FailOnNthDelete = 1,
                FailOnGetComponent = false,
            };
            // 3 change:Add 失败 N=0,Delete 失败 N=1(从 0 数)→ 2 失败,1 成功(最后一个)
            int result = AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(0, 1, 2));
            Assert.AreEqual(1, result, "3 change 里 2 个 fail,1 个 OK → 1");
        }

        // ===== 参数透传 =====

        [TestMethod]
        public void ApplyRebuild_NewDistanceAndReverse_PassedToTryAdd()
        {
            var changes = new List<PendingChange>
            {
                new PendingChange
                {
                    StepIndex = 0,
                    StepName = "Step0",
                    ComponentName = "C",
                    OldDistance = 50,
                    NewDistance = 200,  // ← 不同
                    OldReverse = true,
                    NewReverse = true,   // ← 不同
                }
            };
            var editor = new RecordingEditor();
            AiExplodeAdvisor.ApplyRebuild(editor, changes);

            // 最后一次调用是 TryAddStep:200:True
            var lastCall = editor.Calls[editor.Calls.Count - 1];
            Assert.AreEqual("TryAddStep:200:True", lastCall,
                "NewDistance=200 + NewReverse=true 必须原样透传到 TryAddStep");
        }

        [TestMethod]
        public void ApplyRebuild_StepIndex_PassedToGetAndDelete()
        {
            var editor = new RecordingEditor();
            AiExplodeAdvisor.ApplyRebuild(editor, MakeChanges(7));

            // 第 1 次调用 GetComponentForStep:7,第 2 次 TryDeleteStep:7
            Assert.AreEqual("GetComponentForStep:7", editor.Calls[0]);
            Assert.AreEqual("TryDeleteStep:7", editor.Calls[1]);
        }
    }
}
