'Imports System.Reflection
'Imports Microsoft.VisualBasic.DataMining.HMM
'Imports Microsoft.VisualBasic.DataMining.HMM.Model
'Imports Microsoft.VisualBasic.Serialization.JSON

Public Module Main

    '    Public Function TestData() As JsonHMM
    '        Dim inf As New modelInfo With {
    '            .name = NameOf(TestData),
    '            .date = Now.ToString,
    '            .title = MethodBase.GetCurrentMethod.GetFullName
    '        }
    '        Dim data As New modelData With {
    '            .emissions = {"R->F->0.4", "R->U->0.5", "R->D->0.1", "S->F->0.4", "S->U->0.0", "S->D->0.6", "C->F->0.4", "C->U->0.2", "C->D->0.4"},
    '            .transitions = {"R->R->0.2", "R->S->0.1", "R->C->0.7", "S->R->0.3", "S->S->0.4", "S->C->0.3", "C->R->0.1", "C->S->0.4", "C->C->0.5"},
    '            .init_props = New Dictionary(Of String, Double) From {{"R", 0.3}, {"S", 0.4}, {"C", 0.3}},
    '            .observations = {"F", "U", "D"},
    '            .states = {"R", "S", "C"}
    '        }
    '        Return New JsonHMM With {.modelInfo = inf, .modelData = data}
    '    End Function

    Public Sub Main()
        Call testProxy.runHttp()

        ' Call streamTest.Main2().GetAwaiter.GetResult()

        ' Call Llamatest.Main2()
        '        Call HMMTextGenerator.Main33()
        '        Call Module2.HMM2()

        '        ' Call Module1.Main2()
        '        Call Module1.Main2()


        '        Dim jp As JsonHMM = "G:\HMM\test\test_HMM.json".ReadAllText.LoadJSON(Of JsonHMM)
        '        Dim name As String = jp.modelInfo.name
        '        Dim states As List(Of String) = New List(Of String)(jp.modelData.states)
        '        Dim observations As List(Of String) = New List(Of String)(jp.modelData.observations)
        '        Dim initialProbabilities As Dictionary(Of String, Double) = jp.modelData.init_props
        '        Dim transitionMatrix As Dictionary(Of String, Double) = DataDecoding.GetMatrix(jp.modelData.transitions)
        '        Dim emissionMatrix As Dictionary(Of String, Double) = DataDecoding.GetMatrix(jp.modelData.emissions)

        '        Dim hmm As New HiddenMarkovModel(name, states, observations, initialProbabilities, transitionMatrix, emissionMatrix)
        '        Dim sampleStates = New List(Of String)
        '        sampleStates.Add("R")
        '        sampleStates.Add("S")
        '        sampleStates.Add("R")
        '        sampleStates.Add("R")
        '        sampleStates.Add("S")
        '        sampleStates.Add("R")
        '        sampleStates.Add("R")
        '        sampleStates.Add("S")
        '        sampleStates.Add("R")
        '        sampleStates.Add("R")
        '        sampleStates.Add("S")
        '        sampleStates.Add("R")

        '        ' sampleStates.add("R");

        '        Dim sampleO As New List(Of String)
        '        sampleO.Add("U")
        '        sampleO.Add("D")
        '        sampleO.Add("U")
        '        sampleO.Add("U")
        '        sampleO.Add("D")
        '        sampleO.Add("U")
        '        sampleO.Add("U")
        '        sampleO.Add("D")
        '        sampleO.Add("U")
        '        sampleO.Add("U")
        '        sampleO.Add("D")
        '        sampleO.Add("U")
        '        'sampleO.add("U");

        '        'sampleO.add("U");
        '        'sampleO.add("D");
        '        '  sampleO.add("U");

        '        Console.WriteLine(hmm.evaluateUsingBruteForce(sampleStates, sampleO))
        '        Console.WriteLine(hmm.evaluateUsingForwardAlgorithm(sampleStates, sampleO))
        '        Console.WriteLine(hmm.getOptimalStateSequenceUsingViterbiAlgorithm(states, sampleO))
        '        hmm.estimateParametersUsingBaumWelchAlgorithm(states, sampleO, False)
        '        Console.WriteLine(hmm.InitialProbabilities.GetJson)
        '        Console.WriteLine(hmm.TransitionMatrix.GetJson)
        '        Console.WriteLine(hmm.EmissionMatrix.GetJson)
        '        Console.WriteLine(hmm.evaluateUsingBruteForce(sampleStates, sampleO))
        '        Console.WriteLine(hmm.evaluateUsingForwardAlgorithm(sampleStates, sampleO))

        '        Pause()
    End Sub
End Module